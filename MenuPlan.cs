using System.IO.Enumeration;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

[SupportedOSPlatform("windows")]
internal sealed record ShortcutCandidate(MenuRoot Root, FileInfo File, string DestinationName,
    ShortcutInfo? Link, string? UrlContent)
{
    public string? RepairArguments { get; set; }
    public string? RepairIconPath { get; set; }
    public string? OriginalHash { get; init; }
    public string? PreserveDirectory { get; set; }
    public string Destination => Path.Combine(PreserveDirectory ?? Root.Path, DestinationName);

    public static ShortcutCandidate Read(MenuRoot root, FileInfo file, string name)
    {
        string? before = BackupSession.Hash(file.FullName);
        ShortcutCandidate result;
        if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            result = new(root, file, name, ShellLinkReader.TryRead(file.FullName, out var info) ? info : null, null);
        else result = new(root, file, name, null, System.IO.File.ReadAllText(file.FullName));
        if (before is null || BackupSession.Hash(file.FullName) != before)
            throw new IOException($"Shortcut changed while reading: {file.FullName}");
        return result with { OriginalHash = before };
    }
}

internal sealed record MenuAction(string Kind, string Source, string? Destination, string Reason,
    string? Arguments = null, string? IconPath = null, string? OriginalHash = null);

[SupportedOSPlatform("windows")]
internal sealed class MenuPlan
{
    private static readonly Regex VersionDirectory = new(
        @"(?<=\\)(?<prefix>app-)?(?<version>\d+(?:\.\d+){1,3})(?=\\|$)", RegexOptions.IgnoreCase);
    private static readonly Regex WslId = new(
        @"(?<prefix>--distribution-id\s+)(?<id>\{[a-f0-9-]+\})", RegexOptions.IgnoreCase);
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    public List<MenuAction> Actions { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public int EmptyFoldersRemoved { get; private set; }
    public int AppliedActions { get; private set; }
    public Dictionary<string, string?> Preconditions { get; } = new(Paths);

    public static MenuPlan Create(List<ShortcutCandidate> candidates, List<MenuAction> removals, Options options,
        IReadOnlyDictionary<string, string>? distributions = null)
    {
        var plan = new MenuPlan();
        plan.Actions.AddRange(removals);
        distributions ??= ReadDistributions(plan.Diagnostics);
        var wslFolders = new HashSet<string>(Paths);
        foreach (var candidate in candidates)
        {
            string? top = TopFolder(candidate);
            if (options.PreserveWslFolders && top is not null && candidate.Link is { } link
                && Path.GetFileName(link.TargetPath).Equals("wslg.exe", StringComparison.OrdinalIgnoreCase))
                wslFolders.Add(top);
        }
        foreach (var candidate in candidates)
        {
            string? top = TopFolder(candidate);
            if (top is not null && (wslFolders.Contains(top) || IsPreserved(candidate, options)))
                candidate.PreserveDirectory = candidate.File.DirectoryName;
            InspectWrapper(candidate, options, distributions, plan.Diagnostics);
        }
        foreach (var nameGroup in candidates.GroupBy(c => c.DestinationName, Paths))
            if (nameGroup.Select(c => c.Root.Path).Distinct(Paths).Count() > 1
                && nameGroup.Select(c => LaunchKey(c, options)).Distinct(StringComparer.Ordinal).Count() > 1)
                plan.Diagnostics.Add($"Distinct launch settings across user/system menus; choose manually: {nameGroup.Key}");

        var survivors = new List<ShortcutCandidate>();
        // An ordinal key keeps case-sensitive arguments distinct; only paths are folded.
        foreach (var group in candidates.GroupBy(c => c.DestinationName.ToUpperInvariant() + "\0" + LaunchKey(c, options)))
        {
            var members = group.ToList();
            if (!options.DeduplicateShortcuts || LaunchKey(members[0], options) is null)
            {
                survivors.AddRange(members);
                continue;
            }
            var valid = members.Where(HasValidLaunchPaths).ToList();
            if (valid.Count == 0) { survivors.AddRange(members); continue; }
            var keeper = valid.OrderByDescending(c => VersionRank(c, options), VersionListComparer.Instance)
                .ThenBy(c => c.RepairArguments is null ? 0 : 1)
                .ThenBy(c => Paths.Equals(c.File.FullName, c.Destination) ? 0 : 1)
                .ThenBy(c => c.Root.Label, StringComparer.Ordinal)
                .ThenBy(c => c.File.FullName, Paths).First();
            survivors.Add(keeper);
            foreach (var duplicate in members.Where(c => c != keeper))
            {
                // An unavailable newer installation is diagnostic, not permission to remove it.
                if (VersionListComparer.Instance.Compare(VersionRank(duplicate, options), VersionRank(keeper, options)) > 0)
                {
                    survivors.Add(duplicate);
                    continue;
                }
                string reason = $"same launch settings as {keeper.File.FullName}";
                if (!VersionRank(duplicate, options).SequenceEqual(VersionRank(keeper, options)))
                    reason = $"superseded by newer valid launch paths in {keeper.File.FullName}";
                plan.Actions.Add(new("Deduplicate", duplicate.File.FullName, null, reason));
            }
        }

        var freed = plan.Actions.Where(a => a.Kind is "Remove" or "Deduplicate")
            .Select(a => a.Source).ToHashSet(Paths);
        var reserved = new HashSet<string>(Paths);
        foreach (var candidate in survivors.OrderBy(c => Paths.Equals(c.File.FullName, c.Destination) ? 0 : 1)
            .ThenBy(c => c.File.FullName, Paths))
        {
            if (candidate.RepairArguments is not null)
                plan.Actions.Add(new("Repair", candidate.File.FullName, null,
                    "replace unregistered WSL distribution ID with the registered distribution matching the shortcut name",
                    candidate.RepairArguments, candidate.RepairIconPath));
            string destination = candidate.Destination;
            if (Paths.Equals(candidate.File.FullName, destination))
            {
                reserved.Add(destination);
                continue;
            }
            bool Available(string path) => !reserved.Contains(path) && !Directory.Exists(path)
                && (!File.Exists(path) || freed.Contains(path));
            if (!Available(destination))
            {
                string suffix = candidate.File.DirectoryName == candidate.Root.Path
                    ? "alternate" : candidate.File.Directory!.Name;
                string basename = Path.GetFileNameWithoutExtension(candidate.DestinationName);
                string extension = Path.GetExtension(candidate.DestinationName);
                int counter = 1;
                do
                {
                    string label = counter == 1 ? suffix : $"{suffix} {counter}";
                    destination = Path.Combine(Path.GetDirectoryName(candidate.Destination)!, $"{basename} ({label}){extension}");
                    counter++;
                } while (!Available(destination));
                plan.Diagnostics.Add($"Name conflict: preserving distinct shortcut {candidate.File.FullName} as {destination}");
            }
            string kind = Paths.Equals(candidate.File.DirectoryName, Path.GetDirectoryName(destination)) ? "Rename" : "Move";
            bool replacement = freed.Contains(destination) && File.Exists(destination);
            plan.Actions.Add(new(replacement ? "Replace" : kind, candidate.File.FullName, destination,
                replacement ? "replace shortcut already scheduled for removal or consolidation" : "organize shortcut"));
            reserved.Add(destination);
            freed.Add(candidate.File.FullName);
        }
        var repairs = plan.Actions.Where(action => action.Kind == "Repair").ToList();
        plan.Actions.RemoveAll(action => action.Kind == "Repair");
        plan.Actions.InsertRange(0, repairs);
        // Include keepers too: a duplicate must not be deleted if its retained shortcut changed.
        foreach (string path in candidates.Select(c => c.File.FullName).Concat(plan.Actions.SelectMany(a => a.Destination is null
            ? new[] { a.Source } : new[] { a.Source, a.Destination })).Distinct(Paths))
            plan.Preconditions[path] = BackupSession.Hash(path);
        foreach (var candidate in candidates) plan.Preconditions[candidate.File.FullName] = candidate.OriginalHash;
        foreach (var removal in removals.Where(action => action.OriginalHash is not null))
            plan.Preconditions[removal.Source] = removal.OriginalHash;
        return plan;
    }

    private static string? TopFolder(ShortcutCandidate candidate)
    {
        string relative = Path.GetRelativePath(candidate.Root.Path, candidate.File.FullName);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return null;
        string[] pieces = relative.Split(Path.DirectorySeparatorChar);
        return pieces.Length > 1 ? Path.Combine(candidate.Root.Path, pieces[0]) : null;
    }

    private static bool IsPreserved(ShortcutCandidate candidate, Options options)
    {
        string relative = Path.GetRelativePath(candidate.Root.Path, candidate.File.DirectoryName!);
        return relative.Split(Path.DirectorySeparatorChar).Any(piece => options.PreservedFolders.Any(pattern =>
            FileSystemName.MatchesSimpleExpression(pattern, piece, ignoreCase: true)));
    }

    private static string FoldPath(string path) => Environment.ExpandEnvironmentVariables(path).TrimEnd('\\').ToUpperInvariant();
    private static string FamilyPath(string path, bool versions) => versions
        ? VersionDirectory.Replace(FoldPath(path), "${prefix}{VERSION}") : FoldPath(path);

    private static string? LaunchKey(ShortcutCandidate c, Options options)
    {
        if (c.UrlContent is not null) return "url\0" + c.UrlContent;
        if (c.Link is not { } link || string.IsNullOrWhiteSpace(link.TargetPath)) return null;
        string target = Environment.ExpandEnvironmentVariables(link.TargetPath);
        // Advertised links and shell namespace links need more than a filesystem target to compare.
        if ((link.Flags & 0x1000) != 0 || !Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal)
            || (!File.Exists(target) && !Directory.Exists(target))
            || Path.GetExtension(target).Equals(".ico", StringComparison.OrdinalIgnoreCase)) return null;
        bool versions = options.PreferNewerVersions;
        return string.Join("\0", "lnk", FamilyPath(target, versions), c.RepairArguments ?? link.Arguments,
            AssociatedPath(link.WorkingDirectory, target, versions), AssociatedPath(c.RepairIconPath ?? link.IconPath, target, versions), link.IconIndex,
            link.ShowCommand, link.Hotkey, link.Flags, link.AppUserModelId);
    }

    private static string AssociatedPath(string path, string target, bool versions)
    {
        string folded = FoldPath(path);
        if (!versions || folded.Length == 0) return folded;
        string foldedTarget = FoldPath(target);
        Match targetVersion = VersionDirectory.Match(foldedTarget);
        string applicationRoot = targetVersion.Success ? foldedTarget[..targetVersion.Index]
            : Path.GetDirectoryName(foldedTarget)! + "\\";
        if (!folded.StartsWith(applicationRoot, StringComparison.OrdinalIgnoreCase)) return folded;
        // A stable launcher may have app-1.2 working/icon directories (Squirrel-style updates).
        // Arbitrary numbered user workspaces are not evidence of application versions.
        return VersionDirectory.Replace(folded, match => targetVersion.Success || match.Groups["prefix"].Success
            ? match.Groups["prefix"].Value + "{VERSION}" : match.Value);
    }

    private static bool HasValidLaunchPaths(ShortcutCandidate c) => c.Link is null
        || string.IsNullOrEmpty(c.Link.WorkingDirectory)
        || Directory.Exists(Environment.ExpandEnvironmentVariables(c.Link.WorkingDirectory));

    private static List<Version> VersionRank(ShortcutCandidate c, Options options)
    {
        if (!options.PreferNewerVersions || c.Link is null) return [];
        return VersionDirectory.Matches(c.Link.TargetPath + "\\" + c.Link.WorkingDirectory + "\\" + c.Link.IconPath)
            .Select(match => Version.Parse(match.Groups["version"].Value)).ToList();
    }

    private sealed class VersionListComparer : IComparer<List<Version>>
    {
        public static readonly VersionListComparer Instance = new();
        public int Compare(List<Version>? left, List<Version>? right)
        {
            if (left is null || right is null) return 0;
            for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
            {
                int comparison = left[i].CompareTo(right[i]);
                if (comparison != 0) return comparison;
            }
            return left.Count.CompareTo(right.Count);
        }
    }

    private static Dictionary<string, string> ReadDistributions(List<string> diagnostics)
    {
        var result = new Dictionary<string, string>(Paths);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (key is null) return result;
            foreach (string id in key.GetSubKeyNames())
            {
                using var distribution = key.OpenSubKey(id);
                if (distribution?.GetValue("DistributionName") is string name) result[id] = name;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            diagnostics.Add($"Could not inspect WSL registrations: {ex.Message}");
        }
        return result;
    }

    private static void InspectWrapper(ShortcutCandidate c, Options options,
        IReadOnlyDictionary<string, string> distributions, List<string> diagnostics)
    {
        if (c.Link is not { } link) return;
        string executable = Path.GetFileName(link.TargetPath);
        if (executable.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("wslg.exe", StringComparison.OrdinalIgnoreCase))
        {
            Match match = WslId.Match(link.Arguments);
            if (match.Success && !distributions.ContainsKey(match.Groups["id"].Value))
            {
                diagnostics.Add($"Unregistered WSL distribution ID: {c.File.FullName}");
                var matches = distributions.Where(pair => pair.Value.Equals(Path.GetFileNameWithoutExtension(c.DestinationName), StringComparison.OrdinalIgnoreCase)).ToList();
                if (options.RepairWslShortcuts && matches.Count == 1)
                {
                    Group id = match.Groups["id"];
                    c.RepairArguments = link.Arguments[..id.Index] + matches[0].Key + link.Arguments[(id.Index + id.Length)..];
                    string updatedIcon = link.IconPath.Replace(id.Value, matches[0].Key, StringComparison.OrdinalIgnoreCase);
                    if (updatedIcon != link.IconPath && File.Exists(Environment.ExpandEnvironmentVariables(updatedIcon)))
                        c.RepairIconPath = updatedIcon;
                }
            }
            if (!match.Success)
            {
                Match named = Regex.Match(link.Arguments, "(?:^|\\s)(?:-d|--distribution)\\s+(?:\"(?<name>[^\"]+)\"|(?<name>\\S+))", RegexOptions.IgnoreCase);
                if (named.Success && !distributions.Values.Contains(named.Groups["name"].Value, StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add($"Unregistered WSL distribution name '{named.Groups["name"].Value}': {c.File.FullName}");
            }
        }
        if (!string.IsNullOrEmpty(link.WorkingDirectory)
            && Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(link.WorkingDirectory))
            && !Directory.Exists(Environment.ExpandEnvironmentVariables(link.WorkingDirectory)))
            diagnostics.Add($"Missing working directory: {c.File.FullName} -> {link.WorkingDirectory}");
        if (executable.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Inspect explicit quoted script paths only. Never execute or decode command payloads.
            foreach (Match script in Regex.Matches(link.Arguments, "[\"'](?<path>[a-zA-Z]:\\\\[^\"']+\\.(?:bat|cmd|ps1))[\"']", RegexOptions.IgnoreCase))
            {
                string path = Environment.ExpandEnvironmentVariables(script.Groups["path"].Value);
                if (!File.Exists(path)) diagnostics.Add($"Missing wrapper script: {c.File.FullName} -> {path}");
            }
        }
    }

    public int Execute(Options options, List<MenuRoot> roots)
    {
        foreach (string diagnostic in Diagnostics) Console.WriteLine($"  Note: {diagnostic}");
        foreach (MenuAction action in Actions)
            Console.WriteLine($"  {action.Kind}: {action.Source}{(action.Destination is null ? "" : " -> " + action.Destination)} ({action.Reason})");
        if (Actions.Count == 0) return 0;
        var touched = TouchedFolders(roots);
        if (!options.Apply)
        {
            if (!options.KeepEmptyFolders) EmptyFoldersRemoved = PreviewEmptyFolders(touched);
            return 0;
        }
        foreach (var expected in Preconditions)
        {
            MenuSafety.ValidatePath(expected.Key, roots.Select(r => Path.GetDirectoryName(r.Path)!).ToArray());
            if (BackupSession.Hash(expected.Key) != expected.Value)
            {
                Console.Error.WriteLine($"Menu changed since planning; no changes applied: {expected.Key}");
                return 1;
            }
        }
        BackupSession backup;
        try
        {
            if (roots.Any(root => MenuSafety.Within(options.BackupDirectory, Path.GetDirectoryName(root.Path)!)
                || Paths.Equals(options.BackupDirectory, Path.GetDirectoryName(root.Path))))
                throw new ArgumentException("Place backupDirectory outside the Start Menu.");
            backup = BackupSession.Create(options.BackupDirectory, Preconditions.Keys, touched);
            backup.Verify(Preconditions.Keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Backup failed; no changes applied: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"Backup: {backup.DirectoryPath}");
        int errors = 0;
        try
        {
            foreach (var action in Actions)
            {
                bool mutationStarted = false;
                try
                {
                    backup.Verify(Preconditions.Keys);
                    foreach (string path in action.Destination is null ? new[] { action.Source } : new[] { action.Source, action.Destination })
                        MenuSafety.ValidatePath(path, roots.Select(root => Path.GetDirectoryName(root.Path)!).ToArray());
                    mutationStarted = true;
                    // Once a step fails, preserve the remaining shortcuts rather than continue a dependent plan.
                    if (action.Kind is "Remove" or "Deduplicate") File.Delete(action.Source);
                    else if (action.Kind == "Repair") ShellLinkReader.SetLaunchData(action.Source, action.Arguments!, action.IconPath);
                    else File.Move(action.Source, action.Destination!, overwrite: false);
                    AppliedActions++;
                    backup.Checkpoint(action.Destination is null ? [action.Source] : [action.Source, action.Destination]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    Console.Error.WriteLine($"Error applying {action.Kind}: {ex.Message}. Remaining actions skipped; use the backup to undo.");
                    if (mutationStarted) backup.Checkpoint(action.Destination is null ? [action.Source] : [action.Source, action.Destination]);
                    errors++;
                    break;
                }
            }
            if (errors == 0 && !options.KeepEmptyFolders)
                foreach (string folder in touched.OrderByDescending(path => path.Length))
                {
                    MenuSafety.ValidatePath(folder, roots.Select(root => Path.GetDirectoryName(root.Path)!).ToArray());
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                        EmptyFoldersRemoved++;
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Error cleaning folders: {ex.Message}");
            errors++;
        }
        finally { backup.Checkpoint([]); }
        return errors;
    }

    private HashSet<string> TouchedFolders(List<MenuRoot> roots)
    {
        var result = new HashSet<string>(Paths);
        foreach (MenuAction action in Actions.Where(a => a.Kind != "Repair"))
        {
            string? folder = Path.GetDirectoryName(action.Source);
            MenuRoot? root = roots.FirstOrDefault(r => MenuSafety.Within(action.Source, r.Path));
            if (root is null) continue;
            while (folder is not null && !Paths.Equals(folder, root.Path))
            {
                result.Add(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }
        return result;
    }

    private int PreviewEmptyFolders(HashSet<string> touched)
    {
        var removed = Actions.Where(a => a.Kind != "Repair").Select(a => a.Source).ToHashSet(Paths);
        var incoming = Actions.Where(a => a.Destination is not null).Select(a => a.Destination!).ToHashSet(Paths);
        int count = 0;
        foreach (string folder in touched.OrderByDescending(path => path.Length))
        {
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder)
                .Any(path => !removed.Contains(path) || incoming.Contains(path))) continue;
            Console.WriteLine($"  Remove empty folder: {folder}");
            removed.Add(folder);
            count++;
        }
        return count;
    }
}
