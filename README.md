# Start Menu Organizer

A small Windows command-line tool that flattens Start Menu folders, tidies shortcut
names, consolidates duplicate launchers, and removes documentation/uninstaller and
dead shortcuts. **It previews changes by default.** Applying changes saves a backup.

## Run

Download and extract the Windows x64 ZIP from [Releases](https://github.com/Taosenai/start-organizer/releases).
The executable includes .NET; no separate runtime installation is needed.
Open a terminal in the extracted folder:

```powershell
.\start-organizer.exe                         # Preview; creates a default config
.\start-organizer.exe --apply                 # Apply the newly computed plan
.\start-organizer.exe --help
```

**Read the proposed removals.** Cleanup terms are intentionally opinionated:
`help`, `docs`, `manual`, and similar words can match shortcuts you want to keep.
Recognized Google Docs launchers are exempt from the generic `docs` term, not from
other removal terms or dead-target checks.

Both the current user's and shared Start Menus are selected by default. Applying
with the shared menu selected requires elevation. Use the **same Windows account**,
working directory, and config for preview and apply; repeat the preview after
changing elevation. Shared-menu changes affect everyone on the machine.
To work only on your account, set `includeGlobal` to `false`.

## Configure

The default file is `start-organizer.config.json` in the **current working directory**,
not necessarily beside the executable. Use an explicit path to avoid surprises:

```powershell
.\start-organizer.exe --config "C:\Tools\start-organizer.config.json"
.\start-organizer.exe --config "C:\Tools\start-organizer.config.json" --apply
```

An explicit config must already exist. Unknown settings and invalid values fail
rather than silently enabling defaults. Existing explicit settings remain honored.

`removeDeadShortcuts` stays enabled by default. Only confirmed missing targets on
available local storage are considered dead; inaccessible/offline targets, network
paths, and reparse points are not evidence of deletion. Term-based cleanup is
independent. `"removeTerms": []` disables term-based cleanup.

`preferNewerVersions` is opt-in and limited to `app-<version>` path components.
Unrelated numbered installation directories are not treated as versions.
See [configuration details](docs/configuration.md) for defaults and folder rules.

## Undo

Every apply prints its backup directory, normally under
`%LOCALAPPDATA%\start-organizer\backups`:

```powershell
.\start-organizer.exe --undo "C:\path\to\backup-run"          # Preview
.\start-organizer.exe --undo "C:\path\to\backup-run" --apply  # Restore
```

Use the original account, elevated for shared-menu changes. Undo verifies saved
bytes and refuses to overwrite later edits. Backups remain until you delete them.
Apply and undo are **not atomic transactions**: close installers/updaters while
running them. A forced termination can require manual recovery from the backup.

## Scope

Duplicate consolidation stays within each menu scope; a per-user shortcut never
replaces a shared launcher. WSL repairs use only the current user's shortcuts and
registrations. Startup is always protected. WSL GUI groups and configured tool
folders stay grouped. Ignored folders and reparse points are not traversed.

This tool handles `.lnk` and `.url` files, not pinned Start entries, packaged-app
registration, or installed applications themselves. Windows and application updates
may recreate shortcuts.

## Build

On Windows with the .NET 10 SDK:

```powershell
dotnet run --project .\tests\StartOrganizer.Tests.csproj -c Release
pwsh -File .\scripts\Build-Release.ps1
```

Tests use temporary menus, never your real ones. The release script builds, tests,
publishes, checks the extracted ZIP, and writes the ZIP plus `SHA256SUMS.txt` to
`artifacts`. See the [release checklist](docs/releasing.md) before distributing it.
