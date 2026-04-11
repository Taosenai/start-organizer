using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("start-organizer only runs on Windows.");
    return 1;
}

try
{
    return RunOnWindows(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

[SupportedOSPlatform("windows")]
static int RunOnWindows(string[] args)
{
    if (args.Length == 1
        && (args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase)))
    {
        PrintUsage();
        return 0;
    }

    if (args.Length > 1 || (args.Length == 1 && !args[0].Equals("--apply", StringComparison.OrdinalIgnoreCase)))
    {
        throw new ArgumentException(
            "Unsupported command-line option. Use --apply to make changes, --help for usage, or edit start-organizer.config.json for all other settings.");
    }

    bool apply = args.Length == 1;
    Options options = Options.LoadOrCreateDefaultConfig(apply);
    var organizer = new Organizer(options);
    return organizer.Run();
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        start-organizer flattens Windows Start Menu shortcuts.

        Usage:
          start-organizer [--apply]
          start-organizer --help

        Options:
          --apply      Make changes. Without this, a dry run is printed.
          -h, --help   Show help.

        Configuration:
          start-organizer reads start-organizer.config.json from the current directory.
          If the file does not exist, it writes a default config there and continues.

          All runtime settings except --apply are controlled by that config file.
        """);
}

[SupportedOSPlatform("windows")]
internal sealed class Organizer
{
    private static readonly Regex VersionSuffixPattern = new(
        @"\s+(?:v(?:ersion)?\s*)?\d+(?:[._-]\d+)+(?:[a-z]\d*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArchitectureSuffixPattern = new(
        @"\s+\((?:x64|x86)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly NormalizedRemovalTerm[] RemoveOnlyTerms =
    [
        new("remove", "remove")
    ];

    private readonly Options _options;
    private readonly List<NormalizedRemovalTerm> _removalTerms;

    public Organizer(Options options)
    {
        _options = options;
        _removalTerms = NormalizeRemovalTerms(options.RemovalTerms);
    }

    public int Run()
    {
        var total = new Stats();

        Console.WriteLine(_options.Apply
            ? "Applying Start Menu changes."
            : "Dry run only. Re-run with --apply to move or delete shortcuts.");

        if (_options.ConfigPath is not null)
        {
            Console.WriteLine($"Config: {_options.ConfigPath}");
        }

        if (_options.Apply && _options.IncludeGlobal && !IsAdministrator())
        {
            Console.WriteLine("Note: the global Start Menu usually requires an elevated terminal.");
        }

        foreach (MenuRoot root in GetMenuRoots(_options))
        {
            Stats stats = ProcessRoot(root);
            total.Add(stats);
        }

        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Shortcuts scanned: {total.ShortcutsScanned}");
        Console.WriteLine($"  Removed: {total.Removed}");
        Console.WriteLine($"  Moved: {total.Moved}");
        Console.WriteLine($"  Renamed: {total.Renamed}");
        Console.WriteLine($"  Empty folders removed: {total.EmptyFoldersRemoved}");
        Console.WriteLine($"  Ignored folders: {total.IgnoredFolders}");
        Console.WriteLine($"  Errors: {total.Errors}");

        return total.Errors == 0 ? 0 : 2;
    }

    private Stats ProcessRoot(MenuRoot root)
    {
        var stats = new Stats();

        Console.WriteLine();
        Console.WriteLine($"{root.Label}: {root.Path}");

        if (!Directory.Exists(root.Path))
        {
            Console.WriteLine("  Skipped: folder does not exist.");
            return stats;
        }

        List<FileInfo> shortcuts = EnumerateShortcuts(root, stats).ToList();
        var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var freedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touchedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FileInfo shortcut in shortcuts)
        {
            stats.ShortcutsScanned++;

            ShortcutDisposition disposition = ClassifyShortcut(shortcut, stats, out string reason);
            if (disposition == ShortcutDisposition.Skip)
            {
                continue;
            }

            if (disposition == ShortcutDisposition.Remove)
            {
                RemoveShortcut(root, shortcut, reason, stats, freedDestinations, touchedFolders);
                continue;
            }

            if (!PathEquals(shortcut.DirectoryName, root.Path))
            {
                MoveShortcutToRoot(root, shortcut, stats, reservedDestinations, freedDestinations, touchedFolders);
                continue;
            }

            if (ShouldRenameShortcut(shortcut))
            {
                RenameShortcutInRoot(root, shortcut, stats, reservedDestinations, freedDestinations);
            }
        }

        if (!_options.KeepEmptyFolders)
        {
            RemoveEmptyFolders(root, stats, touchedFolders, freedDestinations);
        }

        return stats;
    }

    private IEnumerable<FileInfo> EnumerateShortcuts(MenuRoot root, Stats stats)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root.Path));

        while (pending.Count > 0)
        {
            DirectoryInfo current = pending.Pop();

            foreach (FileInfo file in GetFiles(current, stats).OrderBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsShortcut(file) && ShouldInclude(file.Attributes))
                {
                    yield return file;
                }
            }

            List<DirectoryInfo> childrenToVisit = [];
            foreach (DirectoryInfo child in GetDirectories(current, stats).OrderBy(static directory => directory.FullName, StringComparer.OrdinalIgnoreCase))
            {
                if (!ShouldInclude(child.Attributes))
                {
                    if (_options.Verbose)
                    {
                        Console.WriteLine($"  Skip hidden/system folder: {Relative(root, child.FullName)}");
                    }

                    continue;
                }

                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (_options.Verbose)
                    {
                        Console.WriteLine($"  Skip reparse point: {Relative(root, child.FullName)}");
                    }

                    continue;
                }

                if (_options.IgnoredFolders.Contains(child.Name))
                {
                    stats.IgnoredFolders++;
                    if (_options.Verbose)
                    {
                        Console.WriteLine($"  Ignore folder: {Relative(root, child.FullName)}");
                    }

                    continue;
                }

                childrenToVisit.Add(child);
            }

            for (int i = childrenToVisit.Count - 1; i >= 0; i--)
            {
                pending.Push(childrenToVisit[i]);
            }
        }
    }

    private static FileInfo[] GetFiles(DirectoryInfo directory, Stats stats)
    {
        try
        {
            return directory.GetFiles();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            stats.Errors++;
            Console.Error.WriteLine($"  Error reading files in {directory.FullName}: {ex.Message}");
            return [];
        }
    }

    private static DirectoryInfo[] GetDirectories(DirectoryInfo directory, Stats stats)
    {
        try
        {
            return directory.GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            stats.Errors++;
            Console.Error.WriteLine($"  Error reading folders in {directory.FullName}: {ex.Message}");
            return [];
        }
    }

    private ShortcutDisposition ClassifyShortcut(FileInfo shortcut, Stats stats, out string reason)
    {
        string displayName = Path.GetFileNameWithoutExtension(shortcut.Name);
        if (ContainsRemovalTerm(displayName, out string nameTerm))
        {
            reason = $"name contains '{nameTerm}'";
            return ShortcutDisposition.Remove;
        }

        if (shortcut.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            InternetShortcutReadStatus readStatus = TryReadInternetShortcutUrl(shortcut.FullName, out string url, out string readError);
            if (readStatus == InternetShortcutReadStatus.ReadError)
            {
                stats.Errors++;
                Console.Error.WriteLine($"    Error reading URL shortcut {shortcut.FullName}: {readError}");
                reason = string.Empty;
                return ShortcutDisposition.Skip;
            }

            if (readStatus == InternetShortcutReadStatus.FoundUrl)
            {
                if (ContainsRemovalTerm(url, out string urlTerm))
                {
                    reason = $"URL contains '{urlTerm}'";
                    return ShortcutDisposition.Remove;
                }

                if (_options.RemoveDeadShortcuts && LooksLikeDeadInternetShortcut(url, out string deadUrlReason))
                {
                    reason = deadUrlReason;
                    return ShortcutDisposition.Remove;
                }
            }
            else if (readStatus == InternetShortcutReadStatus.MissingUrl && _options.RemoveDeadShortcuts)
            {
                reason = "URL shortcut has no URL";
                return ShortcutDisposition.Remove;
            }
        }

        if (shortcut.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            && ShellLinkReader.TryRead(shortcut.FullName, out ShortcutInfo link))
        {
            if (LooksLikeUninstaller(link, out string uninstallerReason))
            {
                reason = uninstallerReason;
                return ShortcutDisposition.Remove;
            }

            if (LooksLikeDocumentationTarget(link, out string documentationReason))
            {
                reason = documentationReason;
                return ShortcutDisposition.Remove;
            }

            if (_options.RemoveDeadShortcuts && LooksLikeDeadShellLink(link, out string deadShortcutReason))
            {
                reason = deadShortcutReason;
                return ShortcutDisposition.Remove;
            }
        }

        reason = string.Empty;
        return ShortcutDisposition.Keep;
    }

    private bool LooksLikeUninstaller(ShortcutInfo link, out string reason)
    {
        string combined = $"{Path.GetFileNameWithoutExtension(link.TargetPath)} {Path.GetFileName(link.TargetPath)} {link.Arguments} {link.Description}";
        string compact = NormalizeForSearch(combined).Replace(" ", string.Empty, StringComparison.Ordinal);

        foreach (string term in new[] { "uninstall", "uninstaller", "uninst", "unins" })
        {
            if (compact.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"target/arguments contain '{term}'";
                return true;
            }
        }

        if (ContainsAnyNormalized(combined, RemoveOnlyTerms, out string removeTerm))
        {
            reason = $"target/arguments contain '{removeTerm}'";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool LooksLikeDocumentationTarget(ShortcutInfo link, out string reason)
    {
        string extension = Path.GetExtension(link.TargetPath);
        bool documentFile = extension.Equals(".chm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);

        string combined = $"{link.TargetPath} {link.Arguments} {link.Description}";
        if ((documentFile || LooksLikeWebShortcut(link.TargetPath, link.Arguments))
            && ContainsRemovalTerm(combined, out string term))
        {
            reason = $"target/arguments contain '{term}'";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool LooksLikeWebShortcut(string targetPath, string arguments)
    {
        return targetPath.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || targetPath.Contains("https://", StringComparison.OrdinalIgnoreCase)
            || arguments.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || arguments.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDeadShellLink(ShortcutInfo link, out string reason)
    {
        reason = string.Empty;
        string targetPath = Environment.ExpandEnvironmentVariables(link.TargetPath.Trim());

        if (!LooksLikeLocalFilesystemPath(targetPath))
        {
            return false;
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return false;
        }

        reason = $"target does not exist: {targetPath}";
        return true;
    }

    private static bool LooksLikeDeadInternetShortcut(string url, out string reason)
    {
        reason = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            reason = "URL shortcut is malformed";
            return true;
        }

        if (!uri.IsFile)
        {
            return false;
        }

        string targetPath = uri.LocalPath;
        if (!LooksLikeLocalFilesystemPath(targetPath))
        {
            return false;
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return false;
        }

        reason = $"file URL target does not exist: {targetPath}";
        return true;
    }

    private static bool LooksLikeLocalFilesystemPath(string path)
    {
        return path.Length > 0
            && Path.IsPathFullyQualified(path)
            && !path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private void RemoveShortcut(
        MenuRoot root,
        FileInfo shortcut,
        string reason,
        Stats stats,
        HashSet<string> freedDestinations,
        HashSet<string> touchedFolders)
    {
        Console.WriteLine($"  Remove: {Relative(root, shortcut.FullName)} ({reason})");

        if (!_options.Apply)
        {
            stats.Removed++;
            freedDestinations.Add(shortcut.FullName);
            MarkTouchedFolder(root, shortcut, touchedFolders);
            return;
        }

        try
        {
            shortcut.Delete();
            stats.Removed++;
            freedDestinations.Add(shortcut.FullName);
            MarkTouchedFolder(root, shortcut, touchedFolders);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            stats.Errors++;
            Console.Error.WriteLine($"    Error deleting {shortcut.FullName}: {ex.Message}");
        }
    }

    private void MoveShortcutToRoot(
        MenuRoot root,
        FileInfo shortcut,
        Stats stats,
        HashSet<string> reservedDestinations,
        HashSet<string> freedDestinations,
        HashSet<string> touchedFolders)
    {
        string destinationName = GetDestinationFileName(shortcut);
        string destination = GetDestinationPath(
            root,
            shortcut,
            destinationName,
            reservedDestinations,
            freedDestinations,
            BuildFolderSuffix(
                root,
                shortcut.Directory!,
                Path.GetFileNameWithoutExtension(destinationName),
                !shortcut.Name.Equals(destinationName, StringComparison.OrdinalIgnoreCase),
                _options.RemoveVersionNumbers));

        Console.WriteLine($"  Move: {Relative(root, shortcut.FullName)} -> {Path.GetFileName(destination)}");

        if (!_options.Apply)
        {
            stats.Moved++;
            reservedDestinations.Add(destination);
            freedDestinations.Add(shortcut.FullName);
            MarkTouchedFolder(root, shortcut, touchedFolders);
            return;
        }

        try
        {
            File.Move(shortcut.FullName, destination, _options.OverwriteExistingShortcuts);
            stats.Moved++;
            reservedDestinations.Add(destination);
            freedDestinations.Add(shortcut.FullName);
            MarkTouchedFolder(root, shortcut, touchedFolders);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            stats.Errors++;
            Console.Error.WriteLine($"    Error moving {shortcut.FullName}: {ex.Message}");
        }
    }

    private void RenameShortcutInRoot(
        MenuRoot root,
        FileInfo shortcut,
        Stats stats,
        HashSet<string> reservedDestinations,
        HashSet<string> freedDestinations)
    {
        string destinationName = GetDestinationFileName(shortcut);
        string destination = GetDestinationPath(root, shortcut, destinationName, reservedDestinations, freedDestinations, "renamed");
        Console.WriteLine($"  Rename: {shortcut.Name} -> {Path.GetFileName(destination)}");

        if (!_options.Apply)
        {
            stats.Renamed++;
            reservedDestinations.Add(destination);
            freedDestinations.Add(shortcut.FullName);
            return;
        }

        try
        {
            File.Move(shortcut.FullName, destination, _options.OverwriteExistingShortcuts);
            stats.Renamed++;
            reservedDestinations.Add(destination);
            freedDestinations.Add(shortcut.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            stats.Errors++;
            Console.Error.WriteLine($"    Error renaming {shortcut.FullName}: {ex.Message}");
        }
    }

    private string GetDestinationPath(
        MenuRoot root,
        FileInfo shortcut,
        string destinationName,
        HashSet<string> reservedDestinations,
        HashSet<string> freedDestinations,
        string conflictSuffix)
    {
        string direct = Path.Combine(root.Path, destinationName);
        if (IsDestinationAvailable(direct, reservedDestinations, freedDestinations)
            || CanOverwriteDestination(shortcut, direct, reservedDestinations, freedDestinations))
        {
            return direct;
        }

        string baseName = Path.GetFileNameWithoutExtension(destinationName);
        string extension = shortcut.Extension;
        string candidate = Path.Combine(root.Path, $"{baseName} ({conflictSuffix}){extension}");

        int counter = 2;
        while (!IsDestinationAvailable(candidate, reservedDestinations, freedDestinations))
        {
            candidate = Path.Combine(root.Path, $"{baseName} ({conflictSuffix} {counter}){extension}");
            counter++;
        }

        return candidate;
    }

    private static bool IsDestinationAvailable(
        string path,
        HashSet<string> reservedDestinations,
        HashSet<string> freedDestinations)
    {
        return !reservedDestinations.Contains(path)
            && (!File.Exists(path) || freedDestinations.Contains(path))
            && !Directory.Exists(path);
    }

    private bool CanOverwriteDestination(
        FileInfo shortcut,
        string destination,
        HashSet<string> reservedDestinations,
        HashSet<string> freedDestinations)
    {
        if (!_options.OverwriteExistingShortcuts
            || reservedDestinations.Contains(destination)
            || freedDestinations.Contains(destination)
            || PathEquals(shortcut.FullName, destination)
            || Directory.Exists(destination)
            || !File.Exists(destination))
        {
            return false;
        }

        string extension = Path.GetExtension(destination);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldRenameShortcut(FileInfo shortcut)
    {
        return !shortcut.Name.Equals(GetDestinationFileName(shortcut), StringComparison.OrdinalIgnoreCase);
    }

    private string GetDestinationFileName(FileInfo shortcut)
    {
        if (!_options.RemoveVersionNumbers)
        {
            return shortcut.Name;
        }

        string baseName = Path.GetFileNameWithoutExtension(shortcut.Name);
        string cleanedBaseName = RemoveVersionSuffix(baseName);
        return cleanedBaseName.Length == 0 || cleanedBaseName.Equals(baseName, StringComparison.Ordinal)
            ? shortcut.Name
            : $"{cleanedBaseName}{shortcut.Extension}";
    }

    private static string RemoveVersionSuffix(string value)
    {
        string previous;
        string current = value.TrimEnd();

        do
        {
            previous = current;
            current = ArchitectureSuffixPattern.Replace(current, string.Empty).TrimEnd();
            current = VersionSuffixPattern.Replace(current, string.Empty).TrimEnd();
        }
        while (!current.Equals(previous, StringComparison.Ordinal));

        return current;
    }

    private static void MarkTouchedFolder(MenuRoot root, FileInfo shortcut, HashSet<string> touchedFolders)
    {
        string? current = shortcut.DirectoryName;
        while (!string.IsNullOrWhiteSpace(current) && !PathEquals(current, root.Path))
        {
            touchedFolders.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static string BuildFolderSuffix(
        MenuRoot root,
        DirectoryInfo sourceDirectory,
        string destinationBaseName,
        bool destinationNameChanged,
        bool removeVersionNumbers)
    {
        string relative = Path.GetRelativePath(root.Path, sourceDirectory.FullName);
        string[] rawPieces = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        bool suffixChanged = false;
        var pieces = new List<string>();
        foreach (string rawPiece in rawPieces)
        {
            string piece = removeVersionNumbers ? RemoveVersionSuffix(rawPiece) : rawPiece;
            suffixChanged = suffixChanged || !piece.Equals(rawPiece, StringComparison.Ordinal);

            piece = SanitizeFileNamePiece(piece);
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }
        }

        if ((suffixChanged || destinationNameChanged)
            && pieces.Count == 1
            && pieces[0].Equals(destinationBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return "moved";
        }

        return pieces.Count == 0 ? "moved" : string.Join(" - ", pieces);
    }

    private static string SanitizeFileNamePiece(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        return builder.ToString().Trim();
    }

    private void RemoveEmptyFolders(
        MenuRoot root,
        Stats stats,
        HashSet<string> touchedFolders,
        HashSet<string> plannedRemovedFiles)
    {
        List<string> folders = touchedFolders
            .OrderByDescending(static path => path.Length)
            .ToList();
        var plannedRemovedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in folders)
        {
            var directory = new DirectoryInfo(folder);

            if (!directory.Exists)
            {
                continue;
            }

            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || IsWithinIgnoredFolder(root, folder)
                || (!_options.IncludeHidden && IsHiddenOrSystem(directory.Attributes)))
            {
                continue;
            }

            try
            {
                if (!WouldFolderBeEmptyAfterPlannedChanges(folder, plannedRemovedFiles, plannedRemovedFolders))
                {
                    continue;
                }

                Console.WriteLine($"  Remove empty folder: {Relative(root, folder)}");
                if (_options.Apply)
                {
                    Directory.Delete(folder);
                }

                stats.EmptyFoldersRemoved++;
                plannedRemovedFolders.Add(folder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                stats.Errors++;
                Console.Error.WriteLine($"    Error deleting folder {folder}: {ex.Message}");
            }
        }
    }

    private bool WouldFolderBeEmptyAfterPlannedChanges(
        string folder,
        HashSet<string> plannedRemovedFiles,
        HashSet<string> plannedRemovedFolders)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            string fullPath = Path.GetFullPath(entry);
            if (!_options.Apply
                && (plannedRemovedFiles.Contains(fullPath) || plannedRemovedFolders.Contains(fullPath)))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private bool IsWithinIgnoredFolder(MenuRoot root, string path)
    {
        string relative = Path.GetRelativePath(root.Path, path);
        string[] pieces = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return pieces.Any(piece => _options.IgnoredFolders.Contains(piece));
    }

    private bool ContainsRemovalTerm(string text, out string term)
    {
        return ContainsAnyNormalized(text, _removalTerms, out term);
    }

    private static bool ContainsAnyNormalized(string text, IEnumerable<NormalizedRemovalTerm> terms, out string matchingTerm)
    {
        string normalizedText = $" {NormalizeForSearch(text)} ";

        foreach (NormalizedRemovalTerm term in terms)
        {
            if (term.Normalized.Length == 0)
            {
                continue;
            }

            if (normalizedText.Contains($" {term.Normalized} ", StringComparison.OrdinalIgnoreCase))
            {
                matchingTerm = term.Original;
                return true;
            }
        }

        matchingTerm = string.Empty;
        return false;
    }

    private static List<NormalizedRemovalTerm> NormalizeRemovalTerms(IEnumerable<string> terms)
    {
        var normalizedTerms = new List<NormalizedRemovalTerm>();
        foreach (string term in terms)
        {
            string normalized = NormalizeForSearch(term);
            if (normalized.Length > 0)
            {
                normalizedTerms.Add(new NormalizedRemovalTerm(term, normalized));
            }
        }

        return normalizedTerms;
    }

    private static string NormalizeForSearch(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool lastWasSpace = true;
        bool previousWasLowerOrDigit = false;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (char.IsUpper(c) && previousWasLowerOrDigit && !lastWasSpace)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
                previousWasLowerOrDigit = char.IsLower(c) || char.IsDigit(c);
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
                previousWasLowerOrDigit = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static InternetShortcutReadStatus TryReadInternetShortcutUrl(string path, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    url = line[4..];
                    return InternetShortcutReadStatus.FoundUrl;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return InternetShortcutReadStatus.ReadError;
        }

        return InternetShortcutReadStatus.MissingUrl;
    }

    private bool ShouldInclude(FileAttributes attributes)
    {
        return _options.IncludeHidden || !IsHiddenOrSystem(attributes);
    }

    private static bool IsHiddenOrSystem(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
    }

    private static bool IsShortcut(FileInfo file)
    {
        return file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? left, string? right)
    {
        return string.Equals(
            Path.GetFullPath(left ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(MenuRoot root, string path)
    {
        try
        {
            return Path.GetRelativePath(root.Path, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static IEnumerable<MenuRoot> GetMenuRoots(Options options)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string global = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft",
                "Windows",
                "Start Menu",
                "Programs");
        }

        if (string.IsNullOrWhiteSpace(global))
        {
            global = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft",
                "Windows",
                "Start Menu",
                "Programs");
        }

        if (options.IncludeLocal)
        {
            yield return new MenuRoot("Local user Start Menu", local);
        }

        if (options.IncludeGlobal)
        {
            yield return new MenuRoot("Global Start Menu", global);
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private enum ShortcutDisposition
    {
        Keep,
        Remove,
        Skip
    }

    private enum InternetShortcutReadStatus
    {
        FoundUrl,
        MissingUrl,
        ReadError
    }

    private sealed record NormalizedRemovalTerm(string Original, string Normalized);
}

internal sealed class Options
{
    private const string DefaultConfigFileName = "start-organizer.config.json";

    private static readonly JsonSerializerOptions ConfigReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions ConfigWriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static readonly string[] DefaultIgnoredFolders =
    [
        "Accessories",
        "Accessibility",
        "Administrative Tools",
        "Maintenance",
        "Startup",
        "Steam",
        "System Tools",
        "Windows Accessories",
        "Windows Administrative Tools",
        "Windows Ease of Access",
        "Windows PowerShell",
        "Chrome Extensions",
        "LibreOffice"
    ];

    public static readonly string[] DefaultRemovalTerms =
    [
        "uninstall",
        "uninstaller",
        "uninst",
        "unins",
        "remove",
        "release notes",
        "release note",
        "readme",
        "read me",
        "changelog",
        "change log",
        "changes",
        "what's new",
        "whats new",
        "documentation",
        "docs",
        "manual",
        "user guide",
        "guide",
        "help",
        "online help",
        "faq",
        "license",
        "eula",
        "website",
        ".com",
        ".url",
        "report a problem"
    ];

    public bool Apply { get; private set; }
    public bool IncludeLocal { get; private set; } = true;
    public bool IncludeGlobal { get; private set; } = true;
    public bool IncludeHidden { get; private set; }
    public bool KeepEmptyFolders { get; private set; }
    public bool RemoveDeadShortcuts { get; private set; } = true;
    public bool RemoveVersionNumbers { get; private set; } = true;
    public bool OverwriteExistingShortcuts { get; private set; } = true;
    public bool Verbose { get; private set; }
    public string? ConfigPath { get; private set; }
    public HashSet<string> IgnoredFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RemovalTerms { get; } = [];

    private Options()
    {
    }

    public static Options LoadOrCreateDefaultConfig(bool apply)
    {
        string configPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, DefaultConfigFileName));

        if (!File.Exists(configPath))
        {
            WriteDefaultConfig(configPath);
        }

        OrganizerConfig config = ReadConfig(configPath);
        Options options = FromConfig(config);
        options.Apply = apply;
        options.ConfigPath = configPath;
        options.Validate();

        return options;
    }

    private static void WriteDefaultConfig(string path)
    {
        string json = JsonSerializer.Serialize(OrganizerConfig.CreateDefault(), ConfigWriteOptions) + Environment.NewLine;

        try
        {
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException($"Could not create default config file {path}: {ex.Message}", ex);
        }

        Console.WriteLine($"Created default config: {path}");
    }

    private static OrganizerConfig ReadConfig(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            RejectApplyInConfig(path, json);

            return JsonSerializer.Deserialize<OrganizerConfig>(json, ConfigReadOptions)
                ?? new OrganizerConfig();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Could not parse config file {path}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ArgumentException($"Could not read config file {path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ArgumentException($"Could not read config file {path}: {ex.Message}", ex);
        }
    }

    private static void RejectApplyInConfig(string path, string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("apply", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The apply setting is not supported in {path}. Use --apply on the command line instead.");
            }
        }
    }

    private static Options FromConfig(OrganizerConfig config)
    {
        var options = new Options
        {
            IncludeLocal = config.IncludeLocal ?? true,
            IncludeGlobal = config.IncludeGlobal ?? true,
            IncludeHidden = config.IncludeHidden ?? false,
            KeepEmptyFolders = config.KeepEmptyFolders ?? false,
            RemoveDeadShortcuts = config.RemoveDeadShortcuts ?? true,
            RemoveVersionNumbers = config.RemoveVersionNumbers ?? true,
            OverwriteExistingShortcuts = config.OverwriteExistingShortcuts ?? true,
            Verbose = config.Verbose ?? false
        };

        bool useDefaultIgnoredFolders = config.UseDefaultIgnoredFolders
            ?? !HasConfiguredValues(config.IgnoredFolders, config.IgnoreFolders, config.Ignores);
        bool useDefaultRemovalTerms = config.UseDefaultRemovalTerms
            ?? !HasConfiguredValues(config.RemoveTerms, config.RemovalTerms);

        if (useDefaultIgnoredFolders)
        {
            AddNames(options.IgnoredFolders, DefaultIgnoredFolders);
        }

        if (useDefaultRemovalTerms)
        {
            AddNames(options.RemovalTerms, DefaultRemovalTerms);
        }

        AddNames(options.IgnoredFolders, config.IgnoredFolders);
        AddNames(options.IgnoredFolders, config.IgnoreFolders);
        AddNames(options.IgnoredFolders, config.Ignores);
        AddNames(options.RemovalTerms, config.RemoveTerms);
        AddNames(options.RemovalTerms, config.RemovalTerms);

        return options;
    }

    private void Validate()
    {
        if (!IncludeLocal && !IncludeGlobal)
        {
            throw new ArgumentException("At least one of includeLocal or includeGlobal must be true in start-organizer.config.json.");
        }
    }

    private static bool HasConfiguredValues(params string[]?[] values)
    {
        foreach (string[]? value in values)
        {
            if (value is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddNames(HashSet<string> target, IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (string value in values)
        {
            foreach (string splitValue in SplitValues(value))
            {
                target.Add(splitValue);
            }
        }
    }

    private static void AddNames(List<string> target, IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (string value in values)
        {
            foreach (string splitValue in SplitValues(value))
            {
                if (!target.Contains(splitValue, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(splitValue);
                }
            }
        }
    }

    private static IEnumerable<string> SplitValues(string raw)
    {
        return raw
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static value => value.Length > 0);
    }
}

internal sealed class OrganizerConfig
{
    public bool? IncludeLocal { get; set; }
    public bool? IncludeGlobal { get; set; }
    public bool? IncludeHidden { get; set; }
    public bool? KeepEmptyFolders { get; set; }
    public bool? RemoveDeadShortcuts { get; set; }
    public bool? RemoveVersionNumbers { get; set; }
    public bool? OverwriteExistingShortcuts { get; set; }
    public bool? Verbose { get; set; }

    public bool? UseDefaultIgnoredFolders { get; set; }
    public bool? UseDefaultRemovalTerms { get; set; }

    [JsonPropertyName("ignoredFolders")]
    public string[]? IgnoredFolders { get; set; }

    [JsonPropertyName("ignoreFolders")]
    public string[]? IgnoreFolders { get; set; }

    [JsonPropertyName("ignores")]
    public string[]? Ignores { get; set; }

    public string[]? RemoveTerms { get; set; }
    public string[]? RemovalTerms { get; set; }

    public static OrganizerConfig CreateDefault()
    {
        return new OrganizerConfig
        {
            IncludeLocal = true,
            IncludeGlobal = true,
            IncludeHidden = false,
            KeepEmptyFolders = false,
            RemoveDeadShortcuts = true,
            RemoveVersionNumbers = true,
            OverwriteExistingShortcuts = true,
            Verbose = false,
            IgnoredFolders = Options.DefaultIgnoredFolders.ToArray(),
            RemoveTerms = Options.DefaultRemovalTerms.ToArray()
        };
    }
}

internal sealed class Stats
{
    public int ShortcutsScanned { get; set; }
    public int Removed { get; set; }
    public int Moved { get; set; }
    public int Renamed { get; set; }
    public int EmptyFoldersRemoved { get; set; }
    public int IgnoredFolders { get; set; }
    public int Errors { get; set; }

    public void Add(Stats other)
    {
        ShortcutsScanned += other.ShortcutsScanned;
        Removed += other.Removed;
        Moved += other.Moved;
        Renamed += other.Renamed;
        EmptyFoldersRemoved += other.EmptyFoldersRemoved;
        IgnoredFolders += other.IgnoredFolders;
        Errors += other.Errors;
    }
}

internal sealed record MenuRoot(string Label, string Path);

internal sealed record ShortcutInfo(string TargetPath, string Arguments, string Description);

[SupportedOSPlatform("windows")]
internal static class ShellLinkReader
{
    public static bool TryRead(string path, out ShortcutInfo info)
    {
        info = new ShortcutInfo(string.Empty, string.Empty, string.Empty);
        object? shellLink = null;

        try
        {
            shellLink = new ShellLinkCom();
            var persistFile = (IPersistFile)shellLink;
            persistFile.Load(path, 0);

            var link = (IShellLinkW)shellLink;
            var target = new StringBuilder(1024);
            var arguments = new StringBuilder(1024);
            var description = new StringBuilder(1024);

            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetDescription(description, description.Capacity);

            info = new ShortcutInfo(target.ToString(), arguments.ToString(), description.ToString());
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (shellLink is not null)
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCom
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
