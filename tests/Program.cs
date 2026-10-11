using System.Runtime.InteropServices;

int passed = 0;
int failed = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine($"PASS: {name}"); passed++; }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL: {name}: {ex}"); failed++; }
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Test("loose root duplicates consolidate within their own menu scope", () =>
{
    using var f = new Fixture();
    var loose = f.Link(f.User, "../App.lnk");
    var root = f.Link(f.User, "App.lnk");
    var global = f.Link(f.System, "Vendor/App.lnk");
    var plan = f.Plan([loose, root, global]);
    Check(plan.Actions.Count(a => a.Kind == "Deduplicate") == 1, "Only the local duplicate should be removed");
    Check(!plan.Actions.Any(a => a.Kind == "Deduplicate" && a.Source == global.File.FullName), "Shared launcher was deleted");
    Check(plan.Actions.Any(a => a.Source == global.File.FullName && a.Destination == Path.Combine(f.System.Path, "App.lnk")), "Shared launcher should stay in the shared menu");
    Check(plan.Actions.All(a => a.Source != root.File.FullName), "Canonical Programs shortcut must survive");
});

Test("different targets, arguments, working directories, icons, or elevation are preserved", () =>
{
    using var f = new Fixture();
    var root = f.Link(f.User, "App.lnk");
    var incoming = f.Link(f.User, "Vendor/App.lnk");
    var alternateExe = Path.Combine(f.DirectoryPath, "other.exe");
    File.WriteAllText(alternateExe, "other");
    foreach (var info in new[] {
        incoming.Link! with { TargetPath = alternateExe },
        incoming.Link! with { Arguments = "--profile personal" },
        incoming.Link! with { WorkingDirectory = f.DirectoryPath },
        incoming.Link! with { IconPath = alternateExe },
        incoming.Link! with { Flags = incoming.Link!.Flags | 0x2000u }
    })
    {
        var plan = f.Plan([root, incoming with { Link = info }]);
        Check(!plan.Actions.Any(a => a.Kind is "Deduplicate" or "Replace"), "Distinct launches cannot be discarded");
        Check(plan.Actions.Single().Destination != root.File.FullName, "Conflict needs an alternate name");
    }
});

Test("real AppUserModelIDs distinguish shortcuts while matching IDs still deduplicate", () =>
{
    using var f = new Fixture();
    var root = f.Link(f.User, "App.lnk");
    var incoming = f.Link(f.User, "Vendor/App.lnk");
    Check(root.Link!.AppUserModelId == "", "Absent ID should read as empty");
    f.SetAppUserModelId(root.File.FullName, "Test.App.First");
    root = ShortcutCandidate.Read(root.Root, root.File, root.DestinationName);
    Check(root.Link!.AppUserModelId == "Test.App.First", "Stored ID was not read from real shortcut");
    Check(!f.Plan([root, incoming]).Actions.Any(a => a.Kind == "Deduplicate"), "Missing ID was merged with an explicit ID");

    f.SetAppUserModelId(incoming.File.FullName, "Test.App.Second");
    incoming = ShortcutCandidate.Read(incoming.Root, incoming.File, incoming.DestinationName);
    Check(!f.Plan([root, incoming]).Actions.Any(a => a.Kind == "Deduplicate"), "Different application identities were merged");

    f.SetAppUserModelId(incoming.File.FullName, "Test.App.First");
    incoming = ShortcutCandidate.Read(incoming.Root, incoming.File, incoming.DestinationName);
    Check(f.Plan([root, incoming]).Actions.Count(a => a.Kind == "Deduplicate") == 1, "Matching identities stopped deduplication");
    ShellLinkReader.SetLaunchData(root.File.FullName, "--updated");
    Check(ShellLinkReader.TryRead(root.File.FullName, out var updated) && updated.AppUserModelId == "Test.App.First",
        "Editing launch data discarded the application identity");
});

Test("newer valid versioned working directory wins regardless of timestamps", () =>
{
    using var f = new Fixture();
    string oldDirectory = Path.Combine(f.DirectoryPath, "app-3.6.9");
    string newDirectory = Path.Combine(f.DirectoryPath, "app-3.6.10");
    Directory.CreateDirectory(newDirectory); // Old directory may already have been removed by updater.
    var old = f.Link(f.User, "App.lnk", workingDirectory: oldDirectory);
    var newer = f.Link(f.User, "Vendor/App.lnk", workingDirectory: newDirectory);
    File.SetLastWriteTimeUtc(old.File.FullName, DateTime.UtcNow.AddYears(1));
    var plan = f.Plan([old, newer], new OrganizerConfig { PreferNewerVersions = true });
    Check(plan.Actions.Any(a => a.Kind == "Deduplicate" && a.Source == old.File.FullName), "Old version must be superseded");
    Check(plan.Actions.Any(a => a.Kind == "Replace" && a.Source == newer.File.FullName), "Replacement must be explicit");
    var disabled = f.Plan([old, newer], new OrganizerConfig { PreferNewerVersions = false });
    Check(!disabled.Actions.Any(a => a.Kind == "Deduplicate"), "Version preference switch must be honored");
});

Test("an unavailable newer working directory is not chosen or silently discarded", () =>
{
    using var f = new Fixture();
    string validDirectory = Path.Combine(f.DirectoryPath, "app-1.0");
    Directory.CreateDirectory(validDirectory);
    var old = f.Link(f.User, "App.lnk", workingDirectory: validDirectory);
    var unavailable = f.Link(f.User, "Vendor/App.lnk", workingDirectory: Path.Combine(f.DirectoryPath, "app-2.0"));
    var plan = f.Plan([old, unavailable], new OrganizerConfig { PreferNewerVersions = true });
    Check(!plan.Actions.Any(a => a.Kind == "Deduplicate"), "Unavailable newer variant must be preserved");
    Check(plan.Diagnostics.Any(d => d.Contains("Missing working directory")), "Need diagnostic");
});

Test("numbered user working directories are not mistaken for app versions", () =>
{
    using var f = new Fixture();
    string first = Path.Combine(f.DirectoryPath, "workspaces", "1.0");
    string second = Path.Combine(f.DirectoryPath, "workspaces", "2.0");
    Directory.CreateDirectory(first);
    Directory.CreateDirectory(second);
    var a = f.Link(f.User, "App.lnk", workingDirectory: first);
    var b = f.Link(f.User, "Vendor/App.lnk", workingDirectory: second);
    Check(!f.Plan([a, b], new OrganizerConfig { PreferNewerVersions = true }).Actions.Any(action => action.Kind == "Deduplicate"), "User workspaces were discarded");
});

Test("empty removeTerms disables uninstaller and documentation rules", () =>
{
    using var f = new Fixture();
    string uninstaller = Path.Combine(f.DirectoryPath, "unins000.exe");
    File.WriteAllText(uninstaller, "test");
    var link = f.Link(f.User, "App.lnk", target: uninstaller);
    var organizer = new Organizer(Options.FromConfig(new OrganizerConfig { RemoveTerms = [], RemoveDeadShortcuts = false }));
    Check(organizer.ClassifyShortcut(link.File, new Stats(), out _) == Organizer.ShortcutDisposition.Keep, "Uninstaller bypassed config");
    string document = Path.Combine(f.DirectoryPath, "readme.txt");
    File.WriteAllText(document, "test");
    var help = f.Link(f.User, "Documentation.lnk", target: document);
    Check(organizer.ClassifyShortcut(help.File, new Stats(), out _) == Organizer.ShortcutDisposition.Keep, "Documentation bypassed config");
    var enabled = new Organizer(Options.FromConfig(new OrganizerConfig { RemoveTerms = ["unins"] }));
    Check(enabled.ClassifyShortcut(link.File, new Stats(), out _) == Organizer.ShortcutDisposition.Remove, "Configured uninstaller rule stopped working");
});

Test("wslg and pattern-matched tool folders stay grouped but are audited", () =>
{
    using var f = new Fixture();
    string wslg = Path.Combine(f.DirectoryPath, "wslg.exe");
    File.WriteAllText(wslg, "test");
    var linux = f.Link(f.User, "AnyDistro/Linux App.lnk", target: wslg, args: "-d AnyDistro -- app");
    var tools = f.Link(f.System, "Visual Studio 2099/Tools/Compiler.lnk");
    var plan = f.Plan([linux, tools]);
    Check(plan.Actions.Count == 0, "Grouped shortcuts should stay in place");
    var custom = f.Link(f.User, "SDK/Tools/Debugger.lnk");
    Check(f.Plan([custom], new OrganizerConfig { PreservedFolders = ["SDK"] }).Actions.Count == 0, "Custom grouping failed");
});

Test("WSL ID repair is unique, configurable, and never changes ambiguous names", () =>
{
    using var f = new Fixture();
    string wsl = Path.Combine(f.DirectoryPath, "wsl.exe");
    File.WriteAllText(wsl, "test");
    var c = f.Link(f.User, "Distro.lnk", target: wsl, args: "--distribution-id {11111111-1111-1111-1111-111111111111} --cd ~");
    const string registered = "{22222222-2222-2222-2222-222222222222}";
    var registry = new Dictionary<string, string> { [registered] = "Distro" };
    var plan = f.Plan([c], distributions: registry);
    Check(plan.Actions.Single().Arguments!.Contains(registered), "Stale GUID was not repaired");
    Check(f.Plan([c with { RepairArguments = null }], new OrganizerConfig { RepairWslShortcuts = false }, registry).Actions.Count == 0, "Repair switch failed");
    registry["{33333333-3333-3333-3333-333333333333}"] = "Distro";
    Check(f.Plan([c with { RepairArguments = null }], distributions: registry).Actions.Count == 0, "Ambiguous distro was changed");
});

Test("inventory includes loose shortcuts and protects Startup with an empty ignore list", () =>
{
    using var f = new Fixture();
    var loose = f.Link(f.User, "../App.lnk");
    var startup = f.Link(f.User, "Startup/Startup App.lnk");
    var options = Options.FromConfig(new OrganizerConfig { IgnoredFolders = [] });
    var files = new Organizer(options).GatherShortcuts(f.User, new Stats());
    Check(files.Any(file => file.FullName == loose.File.FullName), "Loose shortcut omitted");
    Check(files.All(file => file.FullName != startup.File.FullName), "Startup was scanned");
    bool refused = false;
    try { MenuSafety.ValidatePath(startup.File.FullName, f.AllowedRoots); }
    catch (ArgumentException) { refused = true; }
    Check(refused, "Startup mutation was allowed");
});

Test("real WSL repair preserves other launch settings and can be undone", () =>
{
    using var f = new Fixture();
    string wsl = Path.Combine(f.DirectoryPath, "wsl.exe");
    File.WriteAllText(wsl, "test");
    var c = f.Link(f.User, "Distro.lnk", target: wsl, args: "--distribution-id {11111111-1111-1111-1111-111111111111} --cd ~");
    var before = f.Snapshot();
    const string registered = "{22222222-2222-2222-2222-222222222222}";
    var plan = f.Plan([c], distributions: new Dictionary<string, string> { [registered] = "Distro" });
    Check(plan.Execute(f.Options(true), f.Roots) == 0, "Repair apply failed");
    Check(ShellLinkReader.TryRead(c.File.FullName, out var repaired) && repaired.Arguments.Contains(registered), "Repair was not saved");
    Check(repaired.TargetPath == c.Link!.TargetPath && repaired.ShowCommand == c.Link.ShowCommand, "Repair altered launch settings");
    Check(BackupSession.Undo(Directory.GetDirectories(f.Backups).Single(), true, f.AllowedRoots) == 0, "Repair undo failed");
    Check(before.SequenceEqual(f.Snapshot()), "Repair undo did not restore original bytes");
});

Test("long shell arguments are read without truncation", () =>
{
    using var f = new Fixture();
    string args = "--payload " + new string('x', 1800);
    var c = f.Link(f.User, "Long.lnk");
    // WScript.Shell itself truncates long argument strings; write through IShellLinkW.
    ShellLinkReader.SetLaunchData(c.File.FullName, args);
    c = ShortcutCandidate.Read(c.Root, c.File, c.DestinationName);
    Check(c.Link!.Arguments == args, "Arguments were truncated");
});

Test("dry run writes nothing; apply, undo, and repeated undo restore original bytes", () =>
{
    using var f = new Fixture();
    string oldDirectory = Path.Combine(f.DirectoryPath, "app-1.0");
    string newDirectory = Path.Combine(f.DirectoryPath, "app-2.0");
    Directory.CreateDirectory(oldDirectory);
    Directory.CreateDirectory(newDirectory);
    var old = f.Link(f.User, "App.lnk", workingDirectory: oldDirectory);
    var newer = f.Link(f.User, "Vendor/App.lnk", workingDirectory: newDirectory);
    var before = f.Snapshot();
    var plan = f.Plan([old, newer], new OrganizerConfig { PreferNewerVersions = true });
    Check(plan.Execute(f.Options(false), f.Roots) == 0, "Dry run failed");
    Check(before.SequenceEqual(f.Snapshot()), "Dry run mutated menu");
    Check(!Directory.Exists(f.Backups), "Dry run created backup");
    Check(plan.Execute(f.Options(true), f.Roots) == 0, "Apply failed");
    Check(!Directory.Exists(newer.File.DirectoryName!), "Empty folder not removed");
    var afterCandidates = f.Roots.SelectMany(root => new Organizer(f.Options(false)).GatherShortcuts(root, new Stats())
        .Select(file => ShortcutCandidate.Read(root, file, file.Name))).ToList();
    Check(f.Plan(afterCandidates).Actions.Count == 0, "Repeated organization was not idempotent");
    string backup = Directory.GetDirectories(f.Backups).Single();
    var applied = f.Snapshot();
    Check(BackupSession.Undo(backup, false, f.AllowedRoots) == 0, "Undo preview failed");
    Check(applied.SequenceEqual(f.Snapshot()), "Undo preview mutated menu");
    Check(BackupSession.Undo(backup, true, f.AllowedRoots) == 0, "Undo failed");
    Check(before.SequenceEqual(f.Snapshot()), "Original files were not restored byte-for-byte");
    Check(BackupSession.Undo(backup, true, f.AllowedRoots) == 0, "Repeated undo failed");
});

Test("backup corruption or post-run edits refuse undo before any restoration", () =>
{
    using var f = new Fixture();
    var c = f.Link(f.User, "Vendor/App.lnk");
    var plan = f.Plan([c]);
    Check(plan.Execute(f.Options(true), f.Roots) == 0, "Apply failed");
    string backup = Directory.GetDirectories(f.Backups).Single();
    string moved = Path.Combine(f.User.Path, "App.lnk");
    File.AppendAllText(moved, "user edit");
    var modified = f.Snapshot();
    Check(BackupSession.Undo(backup, true, f.AllowedRoots) == 2, "Post-run edit was overwritten");
    Check(modified.SequenceEqual(f.Snapshot()), "Undo made partial changes after detecting conflict");
    File.AppendAllText(Directory.GetFiles(backup, "*.bin").First(), "corrupt");
    Check(BackupSession.Undo(backup, true, f.AllowedRoots) == 2, "Corrupt backup was accepted");
});

Test("changes after planning abort apply, including changes to a duplicate keeper", () =>
{
    using var f = new Fixture();
    var keeper = f.Link(f.User, "App.lnk");
    var duplicate = f.Link(f.User, "Vendor/App.lnk");
    var plan = f.Plan([keeper, duplicate]);
    File.AppendAllText(keeper.File.FullName, "user edit");
    var before = f.Snapshot();
    Check(plan.Execute(f.Options(true), f.Roots) == 1, "Changed keeper was ignored");
    Check(before.SequenceEqual(f.Snapshot()), "Aborted apply mutated menu");
});

Test("backup failure prevents menu changes", () =>
{
    using var f = new Fixture();
    var c = f.Link(f.User, "Vendor/App.lnk");
    File.WriteAllText(f.Backups, "not a directory");
    var before = f.Snapshot();
    Check(f.Plan([c]).Execute(f.Options(true), f.Roots) == 1, "Backup failure not reported");
    Check(before.SequenceEqual(f.Snapshot()), "Menu mutated without backup");
});

Test("unreadable shortcuts are preserved even when their names match removal terms", () =>
{
    using var f = new Fixture();
    string path = Path.Combine(f.User.Path, "Help.lnk");
    File.WriteAllText(path, "not a valid shell link");
    var stats = new Stats();
    Check(new Organizer(f.Options(false)).ClassifyShortcut(new FileInfo(path), stats, out _) == Organizer.ShortcutDisposition.Skip,
        "Unreadable shortcut was classified for deletion");
    Check(stats.Errors == 1, "Unreadable shortcut was not reported");
});

Test("failed apply stops before later actions and undo recovers the completed portion", () =>
{
    using var f = new Fixture();
    var first = f.Link(f.User, "A/App A.lnk");
    var blocked = f.Link(f.User, "B/App B.lnk");
    var last = f.Link(f.User, "C/App C.lnk");
    var before = f.Snapshot();
    var plan = f.Plan([first, blocked, last]);
    using (File.Open(blocked.File.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(plan.Execute(f.Options(true), f.Roots) == 1, "Locked shortcut should stop apply");
        Check(plan.AppliedActions == 1, "Apply continued after failure");
        Check(File.Exists(last.File.FullName), "Later source was changed");
    }
    Check(BackupSession.Undo(Directory.GetDirectories(f.Backups).Single(), true, f.AllowedRoots) == 0,
        "Partial apply undo failed");
    Check(before.SequenceEqual(f.Snapshot()), "Partial apply did not restore original bytes");
});

ReleaseSafetyTests.Run(Test, Check);

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "start-organizer-tests-" + Guid.NewGuid().ToString("N"));
    public string Backups => Path.Combine(DirectoryPath, "backups");
    public MenuRoot User { get; }
    public MenuRoot System { get; }
    public List<MenuRoot> Roots => [User, System];
    public string[] AllowedRoots => Roots.Select(r => Path.GetDirectoryName(r.Path)!).ToArray();
    private readonly string _target;
    public Fixture()
    {
        User = new MenuRoot("User", Path.Combine(DirectoryPath, "UserMenu", "Programs"));
        System = new MenuRoot("System", Path.Combine(DirectoryPath, "SystemMenu", "Programs"), IsUserScope: false);
        foreach (var root in Roots) Directory.CreateDirectory(root.Path);
        _target = Path.Combine(DirectoryPath, "app.exe");
        File.WriteAllText(_target, "test fixture; never executed");
    }
    public ShortcutCandidate Link(MenuRoot root, string relative, string? target = null, string args = "", string workingDirectory = "")
    {
        string path = Path.GetFullPath(Path.Combine(root.Path, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            shortcut = shell.CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = target ?? _target;
            link.Arguments = args;
            link.WorkingDirectory = workingDirectory;
            link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        return ShortcutCandidate.Read(root, new FileInfo(path), Path.GetFileName(path));
    }
    public Options Options(bool apply) => global::Options.FromConfig(new OrganizerConfig { BackupDirectory = Backups }, apply);
    public void SetAppUserModelId(string path, string id)
    {
        object shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        var value = new ShellLinkProperties.PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(id) };
        try
        {
            var persist = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
            persist.Load(path, 2); // STGM_READWRITE: the fixture changes the property store.
            var store = (ShellLinkProperties.IPropertyStore)shellLink;
            var key = ShellLinkProperties.AppUserModelIdKey;
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
            persist.Save(path, true);
        }
        finally
        {
            ShellLinkProperties.PropVariantClear(ref value);
            Marshal.FinalReleaseComObject(shellLink);
        }
    }
    public MenuPlan Plan(List<ShortcutCandidate> candidates, OrganizerConfig? config = null,
        IReadOnlyDictionary<string, string>? distributions = null) => MenuPlan.Create(candidates, [],
            global::Options.FromConfig(config ?? new OrganizerConfig { BackupDirectory = Backups }), distributions ?? new Dictionary<string, string>());
    public List<string> Snapshot() => AllowedRoots.SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => path + "=" + BackupSession.Hash(path)).ToList();
    public void Dispose()
    {
        string full = Path.GetFullPath(DirectoryPath);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("start-organizer-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture path");
        Directory.Delete(full, recursive: true);
    }
}
