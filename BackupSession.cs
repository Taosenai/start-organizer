using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

[SupportedOSPlatform("windows")]
internal static class MenuSafety
{
    public static string[] StartMenuRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
    ];

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static void ValidatePath(string path, IEnumerable<string> roots)
    {
        string full = Path.GetFullPath(path);
        string? root = roots.FirstOrDefault(r => Within(full, r));
        if (root is null) throw new ArgumentException($"Path is outside the selected Start Menus: {path}");
        string relative = Path.GetRelativePath(root, full);
        if (relative.Split(Path.DirectorySeparatorChar).Any(piece => piece.Equals("Startup", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Startup is protected: {path}");
        RejectReparsePoints(full);
    }

    internal static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException($"Reparse points are protected: {current}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}

internal sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public List<BackupEntry> Files { get; set; } = [];
    public List<BackupFolder> Folders { get; set; } = [];
}

internal sealed class BackupEntry
{
    public string Path { get; set; } = "";
    public string? Blob { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public FileAttributes Attributes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
}

internal sealed record BackupFolder(string Path, FileAttributes Attributes);

[SupportedOSPlatform("windows")]
internal sealed class BackupSession
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    private readonly BackupManifest _manifest;

    private BackupSession(string directory, BackupManifest manifest)
    {
        DirectoryPath = directory;
        _manifest = manifest;
    }

    public static string? Hash(string path)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        // Access and sharing errors are not equivalent to an absent shortcut.
    }

    public static BackupSession Create(string parent, IReadOnlyDictionary<string, string?> expected, IEnumerable<string> folders)
    {
        string directory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N"));
        // Backups cannot be placed inside any menu being modified.
        foreach (string path in expected.Keys)
            if (MenuSafety.Within(directory, Path.GetDirectoryName(path)!)
                || directory.Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Place backupDirectory outside the Start Menu.");
        MenuSafety.RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        var manifest = new BackupManifest();
        int index = 0;
        foreach (var pair in expected)
        {
            string path = pair.Key;
            string? hash = pair.Value;
            if (Hash(path) != hash)
                throw new IOException($"Shortcut changed since planning, before backup: {path}");
            var entry = new BackupEntry { Path = path, BeforeHash = hash, AfterHash = hash };
            if (hash is not null)
            {
                entry.Blob = $"{index++}.bin";
                entry.Attributes = File.GetAttributes(path);
                entry.LastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                File.Copy(path, Path.Combine(directory, entry.Blob));
                if (Hash(Path.Combine(directory, entry.Blob)) != hash || Hash(path) != hash)
                    throw new IOException($"Shortcut changed while backing up: {path}");
            }
            manifest.Files.Add(entry);
        }
        manifest.Folders.AddRange(folders.Where(Directory.Exists)
            .Select(path => new BackupFolder(path, File.GetAttributes(path))));
        var result = new BackupSession(directory, manifest);
        result.Verify(expected.Keys);
        result.Save();
        return result;
    }

    public void Verify(IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (BackupEntry entry in _manifest.Files.Where(entry => selected.Contains(entry.Path)))
            if (Hash(entry.Path) != entry.AfterHash)
                throw new IOException($"Shortcut changed during this run: {entry.Path}");
    }

    public void Checkpoint(IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (BackupEntry entry in _manifest.Files.Where(entry => selected.Contains(entry.Path))) entry.AfterHash = Hash(entry.Path);
        Save();
    }

    private void Save()
    {
        string manifest = Path.Combine(DirectoryPath, "manifest.json");
        string temp = manifest + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_manifest, JsonOptions));
        File.Move(temp, manifest, overwrite: true);
    }

    public static int Undo(string directory, bool apply, IEnumerable<string> allowedRoots)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            MenuSafety.RejectReparsePoints(directory);
            MenuSafety.RejectReparsePoints(Path.Combine(directory, "manifest.json"));
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")))
                ?? throw new ArgumentException("Backup manifest is empty.");
            if (manifest.FormatVersion != 1) throw new ArgumentException("Unsupported backup format.");
            if (manifest.Files is null || manifest.Folders is null
                || manifest.Files.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path))
                || manifest.Folders.Any(folder => folder is null || string.IsNullOrWhiteSpace(folder.Path)))
                throw new ArgumentException("Invalid backup entries.");
            if (manifest.Files.Select(entry => Path.GetFullPath(entry.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count
                || manifest.Folders.Select(folder => Path.GetFullPath(folder.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Folders.Count)
                throw new ArgumentException("Duplicate backup paths.");
            string[] roots = allowedRoots.ToArray();
            foreach (var folder in manifest.Folders)
            {
                MenuSafety.ValidatePath(folder.Path, roots);
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException("Backup folders cannot be reparse points.");
            }
            var changed = new List<BackupEntry>();
            foreach (BackupEntry entry in manifest.Files)
            {
                MenuSafety.ValidatePath(entry.Path, roots);
                if (Path.GetExtension(entry.Path).ToLowerInvariant() is not (".lnk" or ".url"))
                    throw new ArgumentException($"Backup entry is not a shortcut: {entry.Path}");
                if (!ValidHash(entry.BeforeHash) || !ValidHash(entry.AfterHash))
                    throw new ArgumentException("Invalid backup hash.");
                if (entry.Blob is not null && entry.BeforeHash is null)
                    throw new ArgumentException("Backup blob has no original hash.");
                if (entry.Blob is not null)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Blob, @"\A[0-9]+\.bin\z"))
                        throw new ArgumentException("Invalid backup blob name.");
                    ValidateBlob(directory, entry);
                }
                else if (entry.BeforeHash is not null) throw new ArgumentException("Backup blob is missing.");
                if (entry.BeforeHash == entry.AfterHash) continue; // Unmodified keepers are never restored.
                string? current = Hash(entry.Path);
                if (Directory.Exists(entry.Path)) throw new ArgumentException($"A directory now occupies a shortcut path: {entry.Path}");
                if (current == entry.BeforeHash) continue; // Already restored; repeat undo is safe.
                if (current != entry.AfterHash)
                    throw new ArgumentException($"Shortcut changed after this run; nothing restored: {entry.Path}");
                changed.Add(entry);
            }
            Console.WriteLine(apply ? "Restoring backup." : "Undo dry run only. Add --apply to restore.");
            foreach (var entry in changed) Console.WriteLine($"  Restore: {entry.Path}");
            foreach (var folder in manifest.Folders.Where(folder => !Directory.Exists(folder.Path)))
                Console.WriteLine($"  Restore folder: {folder.Path}");
            if (!apply) return 0;
            // The preview/preflight can take time. Do not accept edits made while
            // it was being printed as the new baseline for restoration.
            foreach (BackupEntry entry in changed) VerifyUndoEntry(entry, roots);
            foreach (var folder in manifest.Folders.OrderBy(folder => folder.Path.Length))
            {
                MenuSafety.ValidatePath(folder.Path, roots);
                if (!Directory.Exists(folder.Path))
                {
                    Directory.CreateDirectory(folder.Path);
                    File.SetAttributes(folder.Path, folder.Attributes);
                }
            }
            foreach (BackupEntry entry in changed)
            {
                VerifyUndoEntry(entry, roots);
                if (entry.Blob is null) File.Delete(entry.Path);
                else
                {
                    ValidateBlob(directory, entry);
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);
                    VerifyUndoEntry(entry, roots);
                    if (File.Exists(entry.Path)) File.SetAttributes(entry.Path, FileAttributes.Normal);
                    File.Copy(Path.Combine(directory, entry.Blob), entry.Path, overwrite: true);
                    File.SetLastWriteTimeUtc(entry.Path, entry.LastWriteTimeUtc);
                    File.SetAttributes(entry.Path, entry.Attributes);
                }
            }
            Console.WriteLine("Backup restored.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine($"Could not undo: {ex.Message}");
            return 2;
        }
    }

    private static bool ValidHash(string? hash) => hash is null || (hash.Length == 64 && hash.All(Uri.IsHexDigit));

    private static void ValidateBlob(string directory, BackupEntry entry)
    {
        string blobPath = Path.Combine(directory, entry.Blob!);
        MenuSafety.RejectReparsePoints(blobPath);
        if (Hash(blobPath) != entry.BeforeHash)
            throw new ArgumentException($"Backup is missing or corrupt: {entry.Blob}");
    }

    private static void VerifyUndoEntry(BackupEntry entry, string[] roots)
    {
        MenuSafety.ValidatePath(entry.Path, roots);
        if (Hash(entry.Path) != entry.AfterHash)
            throw new ArgumentException($"Shortcut changed during undo; remaining restorations stopped: {entry.Path}");
    }
}
