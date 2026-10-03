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
        string? current = full;
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"Reparse points are protected: {current}");
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

    public static string? Hash(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    public static BackupSession Create(string parent, IEnumerable<string> paths, IEnumerable<string> folders)
    {
        string directory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N"));
        // Backups cannot be placed inside any menu being modified.
        foreach (string path in paths)
            if (MenuSafety.Within(directory, Path.GetDirectoryName(path)!)
                || directory.Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Place backupDirectory outside the Start Menu.");
        Directory.CreateDirectory(directory);
        var manifest = new BackupManifest();
        int index = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? hash = Hash(path);
            var entry = new BackupEntry { Path = path, BeforeHash = hash, AfterHash = hash };
            if (hash is not null)
            {
                entry.Blob = $"{index++}.bin";
                entry.Attributes = File.GetAttributes(path);
                entry.LastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                File.Copy(path, Path.Combine(directory, entry.Blob));
                if (Hash(Path.Combine(directory, entry.Blob)) != hash)
                    throw new IOException($"Shortcut changed while backing up: {path}");
            }
            manifest.Files.Add(entry);
        }
        manifest.Folders.AddRange(folders.Where(Directory.Exists)
            .Select(path => new BackupFolder(path, File.GetAttributes(path))));
        var result = new BackupSession(directory, manifest);
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
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")))
                ?? throw new ArgumentException("Backup manifest is empty.");
            if (manifest.FormatVersion != 1) throw new ArgumentException("Unsupported backup format.");
            string[] roots = allowedRoots.ToArray();
            foreach (var folder in manifest.Folders) MenuSafety.ValidatePath(folder.Path, roots);
            var changed = new List<BackupEntry>();
            foreach (BackupEntry entry in manifest.Files)
            {
                MenuSafety.ValidatePath(entry.Path, roots);
                if (Path.GetExtension(entry.Path).ToLowerInvariant() is not (".lnk" or ".url"))
                    throw new ArgumentException($"Backup entry is not a shortcut: {entry.Path}");
                if (entry.Blob is not null)
                {
                    if (Path.GetFileName(entry.Blob) != entry.Blob || !entry.Blob.EndsWith(".bin", StringComparison.Ordinal))
                        throw new ArgumentException("Invalid backup blob name.");
                    string blobPath = Path.Combine(directory, entry.Blob);
                    if (Hash(blobPath) != entry.BeforeHash) throw new ArgumentException($"Backup is missing or corrupt: {entry.Blob}");
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
            foreach (var folder in manifest.Folders.OrderBy(folder => folder.Path.Length))
            {
                if (!Directory.Exists(folder.Path))
                {
                    Directory.CreateDirectory(folder.Path);
                    File.SetAttributes(folder.Path, folder.Attributes);
                }
            }
            foreach (BackupEntry entry in changed)
            {
                MenuSafety.ValidatePath(entry.Path, roots);
                if (entry.Blob is null) File.Delete(entry.Path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Path)!);
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
}
