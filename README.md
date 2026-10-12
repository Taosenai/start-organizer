# Start Menu Organizer

A small Windows command-line tool that flattens Start Menu folders, tidies shortcut
names, consolidates duplicate launchers, and removes documentation/uninstaller &
dead shortcuts.

## Run

Download and extract the Windows x64 ZIP from [Releases](https://github.com/Taosenai/start-organizer/releases).

Open a terminal in the extracted folder:

```powershell
.\start-organizer.exe --help
.\start-organizer.exe                         # Preview changes
.\start-organizer.exe --apply                 # Apply changes
```

**Read the proposed removals.** Cleanup is opinionated:
`help`, `docs`, `manual`, and similar words can match shortcuts you want to keep.

Both the current user's and shared Start Menus are selected by default. Applying
with the shared menu selected requires elevation. To work only on your account, 
set `includeGlobal` to `false` in the config file.

## Configure

The default file is `start-organizer.config.json` in the **current working directory**.

```powershell
.\start-organizer.exe --config "C:\Tools\start-organizer.config.json"
.\start-organizer.exe --config "C:\Tools\start-organizer.config.json" --apply
```

## Build

On Windows with the .NET 10 SDK:

```powershell
dotnet run --project .\tests\StartOrganizer.Tests.csproj -c Release
pwsh -File .\scripts\Build-Release.ps1
```
