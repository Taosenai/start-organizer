using System.Text;

internal static class ReleaseSafetyTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Invalid input was accepted");
        }

        test("release defaults retain dead/doc cleanup and make version preference opt-in", () =>
        {
            foreach (Options options in new[] { Options.FromConfig(new OrganizerConfig()), Options.FromConfig(OrganizerConfig.CreateDefault()) })
            {
                check(options.RemoveDeadShortcuts, "Dead shortcut cleanup default changed");
                check(options.RemovalTerms.Contains("docs") && options.RemovalTerms.Contains("uninstall"), "Documentation cleanup was disabled");
                check(!options.PreferNewerVersions, "Version preference must be opt-in");
            }
        });

        test("apply and undo preserve launchers in both user and shared menus", () =>
        {
            using var f = new Fixture();
            var local = f.Link(f.User, "App.lnk");
            var shared = f.Link(f.System, "Vendor/App.lnk");
            var before = f.Snapshot();
            var plan = f.Plan([local, shared]);
            check(!plan.Actions.Any(a => a.Kind == "Deduplicate"), "Scopes were merged");
            check(plan.Execute(f.Options(true), f.Roots) == 0, "Apply failed");
            check(File.Exists(local.File.FullName), "User entry disappeared");
            check(BackupSession.Hash(Path.Combine(f.System.Path, "App.lnk")) == shared.OriginalHash, "Shared entry disappeared or changed");
            check(BackupSession.Undo(Directory.GetDirectories(f.Backups).Single(), true, f.AllowedRoots) == 0, "Undo failed");
            check(before.SequenceEqual(f.Snapshot()), "Undo did not restore both menus");
        });

        test("identical URL shortcuts also remain in each menu scope", () =>
        {
            using var f = new Fixture();
            var local = Url(f, f.User, "Site.url", "https://example.com/");
            var shared = Url(f, f.System, "Site.url", "https://example.com/");
            var plan = f.Plan([ShortcutCandidate.Read(f.User, local, local.Name), ShortcutCandidate.Read(f.System, shared, shared.Name)]);
            check(plan.Actions.Count == 0, "Cross-scope URL was discarded");
        });

        test("shared WSL launchers are never repaired using current-user registrations", () =>
        {
            using var f = new Fixture();
            string wsl = Path.Combine(f.DirectoryPath, "wsl.exe");
            File.WriteAllText(wsl, "not executed");
            var link = f.Link(f.System, "Distro.lnk", target: wsl, args: "--distribution-id {11111111-1111-1111-1111-111111111111}");
            var registrations = new Dictionary<string, string> { ["{22222222-2222-2222-2222-222222222222}"] = "Distro" };
            var plan = f.Plan([link], distributions: registrations);
            check(plan.Actions.Count == 0, "Shared distribution was rewritten");
            check(plan.Diagnostics.Any(d => d.Contains("Shared WSL launcher")), "Missing shared-scope explanation");
        });

        test("replanning cannot retain a disabled WSL repair", () =>
        {
            using var f = new Fixture();
            string wsl = Path.Combine(f.DirectoryPath, "wsl.exe");
            File.WriteAllText(wsl, "not executed");
            var link = f.Link(f.User, "Distro.lnk", target: wsl, args: "--distribution-id {11111111-1111-1111-1111-111111111111}");
            var registrations = new Dictionary<string, string> { ["{22222222-2222-2222-2222-222222222222}"] = "Distro" };
            check(f.Plan([link], distributions: registrations).Actions.Any(a => a.Kind == "Repair"), "Setup did not propose repair");
            check(f.Plan([link], new OrganizerConfig { RepairWslShortcuts = false }, registrations).Actions.Count == 0, "Stale repair leaked between plans");
        });

        test("numbered executable directories remain distinct even with version preference enabled", () =>
        {
            using var f = new Fixture();
            string first = Path.Combine(f.DirectoryPath, "1.0", "app.exe");
            string second = Path.Combine(f.DirectoryPath, "2.0", "app.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "one"); File.WriteAllText(second, "two");
            var a = f.Link(f.User, "App.lnk", target: first);
            var b = f.Link(f.User, "Vendor/App.lnk", target: second);
            foreach (bool enabled in new[] { false, true })
                check(!f.Plan([a, b], new OrganizerConfig { PreferNewerVersions = enabled }).Actions.Any(x => x.Kind == "Deduplicate"), "Independent installations were merged");
        });

        test("app-version consolidation requires an explicit opt-in", () =>
        {
            using var f = new Fixture();
            string first = Path.Combine(f.DirectoryPath, "app-1.0");
            string second = Path.Combine(f.DirectoryPath, "app-2.0");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var a = f.Link(f.User, "App.lnk", workingDirectory: first);
            var b = f.Link(f.User, "Vendor/App.lnk", workingDirectory: second);
            check(!f.Plan([a, b]).Actions.Any(x => x.Kind == "Deduplicate"), "Default merged versions");
            check(f.Plan([a, b], new OrganizerConfig { PreferNewerVersions = true }).Actions.Any(x => x.Kind == "Deduplicate"), "Opt-in stopped working");
        });

        test("oversized version components do not crash planning or merge installations", () =>
        {
            using var f = new Fixture();
            string first = Path.Combine(f.DirectoryPath, "app-99999999999999999999.0");
            string second = Path.Combine(f.DirectoryPath, "app-1.0");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var a = f.Link(f.User, "App.lnk", workingDirectory: first);
            var b = f.Link(f.User, "Vendor/App.lnk", workingDirectory: second);
            check(!f.Plan([a, b], new OrganizerConfig { PreferNewerVersions = true }).Actions.Any(x => x.Kind == "Deduplicate"), "Unparseable versions were merged");
        });

        test("Google Docs URL shortcuts survive both name and URL term checks", () =>
        {
            using var f = new Fixture();
            foreach (string name in new[] { "Google Docs.url", "Docs.url", "Writing.url" })
                foreach (string url in new[] { "https://docs.google.com/", "https://docs.google.com/document/", "https://docs.google.com/document/d/example/edit" })
                    check(Classify(f, Url(f, f.User, name, url)) == Organizer.ShortcutDisposition.Keep, $"Removed {name} -> {url}");
        });

        test("Google Docs browser and PWA launchers survive generic docs cleanup", () =>
        {
            using var f = new Fixture();
            foreach (string browser in new[] { "chrome.exe", "chrome_proxy.exe", "msedge_proxy.exe", "brave.exe" })
            {
                string target = Path.Combine(f.DirectoryPath, browser);
                File.WriteAllText(target, "not executed");
                foreach (string args in new[] { "--app=https://docs.google.com/document/", "https://docs.google.com/document/", "--profile-directory=Default --app-id=mpnpojknpmmopombnjdcgaaiekajbnjb", "--app-id=aohghmighlieiainnegkcijnfilokake" })
                {
                    var c = f.Link(f.User, "Docs.lnk", target: target, args: args);
                    check(Classify(f, c.File) == Organizer.ShortcutDisposition.Keep, $"Removed Docs launcher: {browser} {args}");
                }
                var custom = f.Link(f.User, "Google Docs.lnk", target: target, args: "--app-id=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                check(Classify(f, custom.File) == Organizer.ShortcutDisposition.Keep, "User-created Google Docs app was removed");
            }
        });

        test("Google Docs exemption is narrow and does not disable ordinary docs cleanup", () =>
        {
            using var f = new Fixture();
            foreach (string url in new[] { "https://example.com/docs", "https://docs.google.com.evil.invalid/document/", "https://docs.google.com@evil.invalid/document/", "https://docs.google.com/documentation", "https://support.google.com/docs" })
                check(Classify(f, Url(f, f.User, "Google Docs.url", url)) == Organizer.ShortcutDisposition.Remove, $"Exemption incorrectly matched {url}");
            string target = Path.Combine(f.DirectoryPath, "chrome_proxy.exe");
            File.WriteAllText(target, "not executed");
            var generic = f.Link(f.User, "Docs.lnk", target: target, args: "--app-id=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            check(Classify(f, generic.File) == Organizer.ShortcutDisposition.Remove, "All apps named Docs were exempted");
            var ordinary = f.Link(f.User, "Google Docs.lnk");
            check(Classify(f, ordinary.File) == Organizer.ShortcutDisposition.Remove, "Product name alone exempted a non-browser target");
            check(Classify(f, Url(f, f.User, "Google Docs Help.url", "https://docs.google.com/document/")) == Organizer.ShortcutDisposition.Remove, "Help cleanup was disabled");
            check(Classify(f, Url(f, f.User, "Uninstall Google Docs.url", "https://docs.google.com/document/")) == Organizer.ShortcutDisposition.Remove, "Uninstall cleanup was disabled");
        });

        test("Google Docs exemption still allows dead-target cleanup and explicit removal terms", () =>
        {
            using var f = new Fixture();
            string target = Path.Combine(f.DirectoryPath, "chrome_proxy.exe");
            File.WriteAllText(target, "not executed");
            var c = f.Link(f.User, "Docs.lnk", target: target, args: "--app-id=mpnpojknpmmopombnjdcgaaiekajbnjb");
            File.Delete(target);
            check(Classify(f, c.File) == Organizer.ShortcutDisposition.Remove, "Dead browser target bypassed cleanup");
            var file = Url(f, f.User, "Google Docs.url", "https://docs.google.com/document/");
            var organizer = new Organizer(Options.FromConfig(new OrganizerConfig { RemoveTerms = ["google docs"] }));
            check(organizer.ClassifyShortcut(file, new Stats(), out _) == Organizer.ShortcutDisposition.Remove, "Explicit product removal term was ignored");
        });

        test("missing local targets are still removed for links and file URLs", () =>
        {
            using var f = new Fixture();
            string target = Path.Combine(f.DirectoryPath, "gone.exe");
            File.WriteAllText(target, "not executed");
            var link = f.Link(f.User, "Gone.lnk", target: target);
            var url = Url(f, f.User, "Gone.url", new Uri(target).AbsoluteUri);
            File.Delete(target);
            check(LocalTargetProbe.Probe(target) == TargetAvailability.Missing, "Accessible missing target was not recognized");
            foreach (FileInfo file in new[] { link.File, url })
            {
                check(Classify(f, file) == Organizer.ShortcutDisposition.Remove, "Default dead cleanup no longer works");
                var disabled = new Organizer(Options.FromConfig(new OrganizerConfig { RemoveDeadShortcuts = false }));
                check(disabled.ClassifyShortcut(file, new Stats(), out _) == Organizer.ShortcutDisposition.Keep, "Dead cleanup switch ignored");
            }
        });

        test("unavailable drives, denied targets, and ambiguous absence are unknown", () =>
        {
            using var f = new Fixture();
            string target = Path.Combine(f.DirectoryPath, "gone.exe");
            check(LocalTargetProbe.Probe(target, _ => false, _ => throw new InvalidOperationException("Unavailable drive must not be traversed")) == TargetAvailability.Unknown, "Unavailable drive marked missing");
            check(LocalTargetProbe.Probe(target, _ => true, path => path == target ? throw new UnauthorizedAccessException() : File.GetAttributes(path)) == TargetAvailability.Unknown, "Denied target marked missing");
            check(LocalTargetProbe.Probe(target, _ => true, readEntries: _ => throw new UnauthorizedAccessException()) == TargetAvailability.Unknown, "Denied parent marked missing");
            check(LocalTargetProbe.Probe(target, _ => true, readEntries: _ => new[] { target }) == TargetAvailability.Unknown, "Present but unreadable entry marked missing");
            check(LocalTargetProbe.Probe(@"\\server\share\app.exe") == TargetAvailability.Unknown, "UNC target probed as local");
            check(LocalTargetProbe.Probe("shell:AppsFolder") == TargetAvailability.Unknown, "Shell namespace probed as local");
            check(!Organizer.LooksLikeDeadShellLink(new ShortcutInfo(target, "", "", Flags: 0x1000), out _), "Advertised MSI link marked dead");
        });

        test("offline and reparse-point target ancestors are not followed", () =>
        {
            using var f = new Fixture();
            string target = Path.Combine(f.DirectoryPath, "gone.exe");
            foreach (FileAttributes flag in new[] { FileAttributes.ReparsePoint, FileAttributes.Offline })
            {
                FileAttributes Read(string path)
                {
                    if (path.Equals(f.DirectoryPath, StringComparison.OrdinalIgnoreCase)) return FileAttributes.Directory | flag;
                    if (path == target) throw new InvalidOperationException("Unavailable ancestor was followed");
                    return File.GetAttributes(path);
                }
                check(LocalTargetProbe.Probe(target, _ => true, Read) == TargetAvailability.Unknown, "Unavailable ancestor marked missing");
            }
        });

        test("backup creation checks the plan baseline rather than adopting later edits", () =>
        {
            using var f = new Fixture();
            var c = f.Link(f.User, "Vendor/App.lnk");
            var plan = f.Plan([c]);
            File.AppendAllText(c.File.FullName, "edit between preflight and backup");
            var edited = f.Snapshot();
            bool rejected = false;
            try { BackupSession.Create(f.Backups, plan.Preconditions, []); }
            catch (IOException) { rejected = true; }
            check(rejected, "Backup adopted a different baseline from the plan");
            check(edited.SequenceEqual(f.Snapshot()), "Failed backup changed menu contents");
        });

        test("a destination appearing before backup is not accepted as the plan baseline", () =>
        {
            using var f = new Fixture();
            var c = f.Link(f.User, "Vendor/App.lnk");
            var plan = f.Plan([c]);
            string destination = plan.Actions.Single().Destination!;
            File.WriteAllText(destination, "new user shortcut");
            bool rejected = false;
            try { BackupSession.Create(f.Backups, plan.Preconditions, []); }
            catch (IOException) { rejected = true; }
            check(rejected, "New destination was silently incorporated into backup");
            check(File.ReadAllText(destination) == "new user shortcut", "New destination was changed");
        });

        test("shortcut hash errors are not reported as missing files", () =>
        {
            using var f = new Fixture();
            var c = f.Link(f.User, "App.lnk");
            using var held = File.Open(c.File.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            bool rejected = false;
            try { BackupSession.Hash(c.File.FullName); }
            catch (IOException) { rejected = true; }
            check(rejected, "Sharing violation was swallowed as an absent file");
        });

        test("undo rechecks contents after preflight output and refuses concurrent edits", () =>
        {
            using var f = new Fixture();
            var c = f.Link(f.User, "Vendor/App.lnk");
            var plan = f.Plan([c]);
            check(plan.Execute(f.Options(true), f.Roots) == 0, "Apply failed");
            string backup = Directory.GetDirectories(f.Backups).Single();
            string moved = Path.Combine(f.User.Path, "App.lnk");
            List<string>? edited = null;
            TextWriter original = Console.Out;
            using var output = new CallbackWriter(original, text =>
            {
                if (text != "Restoring backup.") return;
                File.AppendAllText(moved, "concurrent user edit");
                edited = f.Snapshot();
            });
            int result;
            try { Console.SetOut(output); result = BackupSession.Undo(backup, true, f.AllowedRoots); }
            finally { Console.SetOut(original); }
            check(result == 2, "Undo accepted an edit after preflight");
            check(edited is not null && edited.SequenceEqual(f.Snapshot()), "Concurrent edit was overwritten or partial restoration occurred");
        });

        test("invalid and null backup collections fail cleanly before restoration", () =>
        {
            using var f = new Fixture();
            Directory.CreateDirectory(f.Backups);
            foreach (string json in new[] { "null", "{\"Files\":null}", "{\"Files\":[null]}", "{\"Folders\":[null]}", "{\"FormatVersion\":999}" })
            {
                File.WriteAllText(Path.Combine(f.Backups, "manifest.json"), json);
                check(BackupSession.Undo(f.Backups, true, f.AllowedRoots) == 2, "Invalid backup accepted");
            }
        });

        test("config rejects typos, non-objects, duplicate keys, and invalid arrays", () =>
        {
            using var f = new Fixture();
            string path = Path.Combine(f.DirectoryPath, "settings.json");
            foreach (string json in new[]
            {
                "{\"includeGobal\":false}", "null", "[]", "42", "{\"includeGlobal\":\"false\"}",
                "{\"includeGlobal\":false,\"IncludeGlobal\":true}", "{\"apply\":false}",
                "{\"removeTerms\":[null]}", "{\"ignoredFolders\":[null]}", "{\"preservedFolders\":[null]}",
                "{\"preservedFolders\":[\" \" ]}", "{\"backupDirectory\":\"\"}",
                "{\"includeLocal\":false,\"includeGlobal\":false}"
            })
            {
                File.WriteAllText(path, json);
                Reject(() => Options.LoadOrCreateDefaultConfig(false, path));
            }
        });

        test("legacy aliases, comments, trailing commas, and explicit settings still work", () =>
        {
            using var f = new Fixture();
            string path = Path.Combine(f.DirectoryPath, "settings.json");
            File.WriteAllText(path, """
                { // Compatibility is intentional.
                  "IncludeGlobal": false,
                  "ignoreFolders": ["One"], "ignores": ["Two"],
                  "useDefaultIgnoredFolders": false,
                  "removalTerms": [], "useDefaultRemovalTerms": false,
                  "preferNewerVersions": true,
                }
                """);
            Options options = Options.LoadOrCreateDefaultConfig(false, path);
            check(!options.IncludeGlobal && options.IgnoredFolders.SetEquals(["One", "Two"]), "Legacy folder aliases changed");
            check(options.RemovalTerms.Count == 0 && options.PreferNewerVersions, "Explicit legacy settings changed");
        });

        test("explicit config is independent of cwd and missing explicit files are never created", () =>
        {
            using var f = new Fixture();
            string path = Path.Combine(f.DirectoryPath, "settings with spaces.json");
            File.WriteAllText(path, "{\"includeGlobal\":false}");
            string other = Path.Combine(f.DirectoryPath, "different-cwd");
            Directory.CreateDirectory(other);
            string old = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = other;
                Options options = Options.LoadOrCreateDefaultConfig(false, path);
                check(!options.IncludeGlobal && options.ConfigPath == path, "Wrong config selected");
                string missing = Path.Combine(other, "typo.json");
                Reject(() => Options.LoadOrCreateDefaultConfig(false, missing));
                check(Directory.GetFiles(other).Length == 0, "Explicit config path caused an unexpected file write");
            }
            finally { Environment.CurrentDirectory = old; }
        });

        test("generated default config retains requested cleanup settings", () =>
        {
            using var f = new Fixture();
            string old = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = f.DirectoryPath;
                Options options = Options.LoadOrCreateDefaultConfig(false);
                check(options.RemoveDeadShortcuts && options.RemovalTerms.Contains("docs") && !options.PreferNewerVersions, "Generated defaults differ from runtime defaults");
                check(File.Exists(Path.Combine(f.DirectoryPath, "start-organizer.config.json")), "Default config was not created");
            }
            finally { Environment.CurrentDirectory = old; }
        });

        test("CLI supports explicit config and rejects ambiguous or incomplete commands", () =>
        {
            var parsed = CommandLineOptions.Parse(["--config", @"C:\settings with spaces.json", "--apply"]);
            check(parsed.Apply && parsed.Config == @"C:\settings with spaces.json", "Config CLI parsing failed");
            check(CommandLineOptions.Parse(["--undo", "backup", "--apply"]).Undo == "backup", "Undo parsing failed");
            check(CommandLineOptions.Parse(["--help"]).Help && CommandLineOptions.Parse(["--version"]).Version, "Informational options failed");
            foreach (string[] args in new string[][]
            {
                ["--config"], ["--config", "--apply"], ["--undo"], ["--undo", "--apply"],
                ["--config", ""], ["--undo", "backup", "--config", "settings.json"],
                ["--config", "a", "--config", "b"], ["--apply", "--apply"], ["--wat"], ["--help", "--apply"]
            }) Reject(() => CommandLineOptions.Parse(args));
        });
    }

    private static FileInfo Url(Fixture fixture, MenuRoot root, string name, string url)
    {
        string path = Path.Combine(root.Path, name);
        File.WriteAllText(path, "[InternetShortcut]\r\nURL=" + url + "\r\n");
        return new FileInfo(path);
    }

    private static Organizer.ShortcutDisposition Classify(Fixture f, FileInfo file) =>
        new Organizer(f.Options(false)).ClassifyShortcut(file, new Stats(), out _);

    private sealed class CallbackWriter(TextWriter inner, Action<string?> callback) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void WriteLine(string? value) { inner.WriteLine(value); callback(value); }
    }
}
