# Start Menu Organizer

`start-organizer` audits and organizes the current user's and machine-wide Windows
Start Menus. It previews a complete plan by default. `--apply` makes the changes
after saving a backup.

## Download

Download the Windows x64 ZIP from the repository's
[latest release](https://github.com/Taosenai/start-organizer/releases/latest), then
extract it into a folder on your machine. The executable includes the .NET runtime;
no separate .NET installation is required. Sign in to GitHub with an account that
can access this private repository.

Run `start-organizer.exe` from a terminal in that folder to preview the changes.
It creates a default config on first run. Use an elevated terminal and `--apply`
when you want to organize both the user and machine-wide menus. The ZIP contains
the executable and this README; your machine-specific config is not included.

## Behavior

- Scans `.lnk` and `.url` files under Programs and directly under Start Menu.
- Flattens ordinary application folders into Programs and removes trailing dotted
  versions and `(x64)` / `(x86)` suffixes from names.
- Consolidates same-name shortcuts across both selected menus when their launch
  settings match. Targets, arguments, working directories, icons, window state,
  hotkeys, link flags, and AppUserModelID participate in comparison. Different
  application IDs (including an ID on only one shortcut) are kept separate.
  URL shortcuts must have
  identical file content. Different launch settings remain separate.
- Prefers newer valid launch paths in an application version family, comparing
  numeric directory versions rather than shortcut timestamps. For a stable launcher,
  versioned working/icon paths must be `app-<version>` directories under that
  application. Arbitrary numbered workspaces remain distinct.
- Prints explicit `Deduplicate`, `Replace`, `Repair`, `Move`, `Rename`, and `Remove`
  actions. Same-name conflicts with different launch settings receive a suffix;
  distinct installs across user/system menus are reported for manual selection.
- Keeps folders containing WSL GUI launchers grouped, along with configurable
  preserved-folder patterns (default: `Visual Studio *`). These folders are still
  audited. Ignored folders are not scanned. **Startup is always protected**, even
  when you replace the default ignore list.
- Detects stale WSL distribution IDs and missing distribution names, working
  directories, and explicit quoted wrapper script paths. It can repair an obsolete
  ID only when exactly one registered distribution matches the shortcut's name.
  A corresponding distribution icon is updated when available. A valid matching
  companion shortcut is preferred over editing a stale copy. Other wrapper
  problems are reported for review; commands are never executed or decoded.
- Removes configured uninstaller/documentation shortcuts and missing local targets.
  Network and shell namespace targets are left alone. Unreadable links are reported
  and preserved. Reparse points are not followed.
- Removes touched folders that become empty, respecting `keepEmptyFolders`.
  Folders containing `desktop.ini` or any other remaining file are retained.

The program does not choose between unrelated installations, assign application-
specific names, or manage packaged applications and pinned Start entries.

## Usage

```powershell
.\start-organizer.exe
.\start-organizer.exe --apply
.\start-organizer.exe --undo "C:\path\to\backup-run"
.\start-organizer.exe --undo "C:\path\to\backup-run" --apply
.\start-organizer.exe --help
```

The machine-wide Start Menu usually requires an elevated terminal for applying or
undoing changes. The plan summary counts proposed actions; apply also reports how
many actions completed. Processing stops at the first failed action.

## Backup and undo

Before changing shortcuts, every inspected candidate and action path is checked
for changes since planning. A backup contains original shortcut bytes, hashes,
attributes, timestamps, and the attributes of touched folders. By default runs are
saved under `%LOCALAPPDATA%\start-organizer\backups`; each apply prints its unique
backup directory. No backup is created for a dry run or an empty plan.

Undo previews restoration unless `--apply` is supplied. It validates backup hashes,
menu boundaries, Startup protection, and current shortcut contents before restoring.
It refuses to overwrite shortcuts edited after the run. Repeated undo is safe.
Restore mixed-scope backups using the same Windows account, elevated if necessary.

Backups are retained until you delete them. If the process is forcibly terminated
between a filesystem operation and its journal checkpoint, undo may report a
conflict; the original `.bin` files remain available for manual recovery. Apply and
undo are filesystem operations, not an atomic transaction.

## Configuration

Settings are read from `start-organizer.config.json` in the current directory.
If absent, a default config is written. The generated file is local and ignored
by Git. Apply belongs on the command line; putting `apply` in JSON is rejected.

New settings default to the following even with an older config:

```json
{
  "includeLocal": true,
  "includeGlobal": true,
  "includeStartMenuRoot": true,
  "includeHidden": false,
  "keepEmptyFolders": false,
  "removeDeadShortcuts": true,
  "removeVersionNumbers": true,
  "deduplicateShortcuts": true,
  "preferNewerVersions": true,
  "preserveWslFolders": true,
  "repairWslShortcuts": true,
  "preservedFolders": ["Visual Studio *"],
  "verbose": false
}
```

Optional `backupDirectory` selects another backup location; environment variables
are expanded. Keep it outside both Start Menus.

`preservedFolders` accepts case-insensitive `*` and `?` patterns against directory
names at any depth. For example, `["Visual Studio *", "SDK Tools"]` preserves both
groups. Use `[]` and `preserveWslFolders: false` to flatten all non-ignored folders.

`ignoredFolders` replaces the default ignore list when supplied. Defaults include
Windows system tools, Startup, Steam, Chrome Extensions, and LibreOffice. Unlike
preserved folders, ignored folders receive no cleanup or deduplication.

`removeTerms` replaces the default removal terms when supplied. Defaults cover
uninstallers, documentation, help, licenses, FAQs, readmes, and release notes.
`"removeTerms": []` disables **all term-based removal**, including uninstaller
target/argument checks; `removeDeadShortcuts` remains independent. Generic `.com`
and `.url` terms are no longer defaults, so ordinary web shortcuts are retained.
Existing custom lists are honored.

At least one of `includeLocal` and `includeGlobal` must be true. Legacy
`ignoreFolders`, `ignores`, `removalTerms`, `useDefaultIgnoredFolders`, and
`useDefaultRemovalTerms` remain accepted. `overwriteExistingShortcuts` is accepted
for compatibility but never permits overwriting an unrelated shortcut.

## Build and verification

```powershell
dotnet build .\StartMenuOrganizer.csproj -c Release
dotnet run --project .\tests\StartOrganizer.Tests.csproj -c Release
dotnet publish .\StartMenuOrganizer.csproj -c Release
```

The dependency-free test runner creates temporary user/system menus and real
Windows links. It exercises conflict preservation, deduplication, version selection,
grouping, WSL repair, Startup protection, config behavior, dry runs, apply/undo,
backup failure, corruption, and post-run edit protection. It never modifies your
actual Start Menus.

The published single executable is at:

```text
bin\Release\net10.0-windows\win-x64\publish\start-organizer.exe
```
