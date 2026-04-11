# Start Menu Organizer

`start-organizer` flattens Windows Start Menu shortcuts from both the current user
and the global machine-wide Start Menu.

By default it performs a dry run. Add `--apply` to move or delete files.

## What It Does

- Scans the current user's Start Menu and the global Start Menu.
- Moves `.lnk` and `.url` shortcuts from subfolders into the base Programs menu.
- Deletes shortcuts that look like uninstallers, release notes, readme files,
  licenses, manuals, help, documentation, guides, changelogs, or FAQs.
- Deletes dead shortcuts whose local filesystem targets no longer exist. Web
  URLs, network paths, and non-filesystem shell links are left alone.
- Removes trailing version numbers and architecture suffixes from shortcut names,
  so names like `Blender 5.1.lnk`, `WebStorm 2026.1.lnk`, and
  `Unity 6000.4.2f1.lnk` become plain product names, and `Krita (x64).lnk`
  becomes `Krita.lnk`.
- Replaces existing root shortcuts when a flattened shortcut wants the same
  filename. Existing directories and duplicate incoming names are still
  protected with conflict suffixes.
- Leaves ignored subfolders alone.
- Removes empty folders after processing unless `"keepEmptyFolders": true`.

## Usage

```powershell
.\start-organizer.exe
.\start-organizer.exe --apply
.\start-organizer.exe --help
```

`--apply` is the only runtime command-line option. Edit
`start-organizer.config.json` in the current directory for all other settings.

The global Start Menu usually requires an elevated terminal when using `--apply`.

## Configuration

The program only reads `start-organizer.config.json` from the current directory.
If that file does not exist, it writes a default config there and continues with
a dry run.

Do not put `apply` in the config file. Use `--apply` on the command line when
you want the program to move or delete shortcuts.

The generated config is intentionally local and should not be checked in.

```json
{
  "includeLocal": true,
  "includeGlobal": true,
  "includeHidden": false,
  "keepEmptyFolders": false,
  "removeDeadShortcuts": true,
  "removeVersionNumbers": true,
  "overwriteExistingShortcuts": true,
  "verbose": false,
  "ignoredFolders": [
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
  ],
  "removeTerms": [
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
  ]
}
```

Set `includeLocal` or `includeGlobal` to `false` to limit which Start Menu root
is processed. At least one must stay `true`.

For older configs, `ignoreFolders`, `ignores`, `removalTerms`,
`useDefaultIgnoredFolders`, and `useDefaultRemovalTerms` are still accepted.

## Build

```powershell
dotnet build .\StartMenuOrganizer.csproj -c Release
dotnet publish .\StartMenuOrganizer.csproj -c Release
```

The single executable is published under:

```text
bin\Release\net10.0-windows\win-x64\publish\start-organizer.exe
```
