# Configuration

The CLI reads `start-organizer.config.json` from the current working directory,
or an existing file passed with `--config <path>`. JSON comments, trailing commas,
and case-insensitive property names are accepted. Unknown properties, duplicate
property names, non-object roots, null/blank array entries, and selecting neither
menu are rejected. `apply` is never accepted in JSON; use `--apply`.

Omitted settings use these defaults (an existing explicit value wins):

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
  "preferNewerVersions": false,
  "preserveWslFolders": true,
  "repairWslShortcuts": true,
  "preservedFolders": ["Visual Studio *"],
  "verbose": false
}
```

`backupDirectory` optionally selects another location, expanding environment
variables. Relative backup paths are relative to the working directory, not the
config file. Keep backups outside the selected Start Menus and away from junctions.
The default is `%LOCALAPPDATA%\start-organizer\backups`.

## Cleanup

`removeTerms` replaces the default removal list. Defaults cover uninstallers,
readmes, release notes, changelogs, documentation/docs, manuals, guides, help, FAQs,
licenses, websites, and problem-reporting shortcuts. Matching is case-insensitive
and word-based, including camel-case splitting. Review the preview: these are
preferences, not a reliable determination of a shortcut's usefulness.

`"removeTerms": []` disables all term-based removal, including uninstaller
checks on targets and arguments. It does not disable dead-shortcut removal.

Google Docs is a specific exception to the **generic `docs` term only**. Recognition
uses its actual `docs.google.com/document/` (or root) URL, known Chromium Docs app
IDs, or an app-ID browser launcher with the full name `Google Docs`. The generic
name `Docs` alone is not enough. Browser/proxy `.lnk` and `.url` forms are covered.
`Google Docs Help`, uninstallers, an explicit `google docs` removal term, and a
missing browser executable still follow the other cleanup rules. This exception
is not a security/authenticity check and does not launch the browser or visit URLs.

`removeDeadShortcuts` remains **true**. The probe distinguishes confirmed absence
on available local storage from inaccessible, disconnected, mapped/network,
offline, and reparse-point paths. Uncertain paths are not removed as dead. A local
shortcut may target a file or directory. Advertised MSI links are not classified
as dead from their filesystem target alone. Missing/malformed `.url` URLs are
still removable. Term-based cleanup remains independent of availability.

`removeVersionNumbers` strips trailing dotted versions and `(x86)` / `(x64)` from
shortcut names. Same-name conflicts with different launch settings get suffixes.
`preferNewerVersions` is **false** unless explicitly enabled. When enabled, it can
consolidate matching launchers across `app-<version>` directories under an
application, choosing newer valid launch paths. Plain `1.0` / `2.0` directory names
are not application identity. Existing configs that explicitly enabled version
preference retain that opt-in.

## Folders and scope

`ignoredFolders` replaces the default exact-name ignore list (case-insensitive).
Defaults include Windows tool groups, Startup, Steam, Chrome Extensions, and
LibreOffice. Ignored folders receive no scan, cleanup, or consolidation. Startup
is protected even with an empty list. These default names are English; review
localized/custom tool groups before applying.

`preservedFolders` accepts case-insensitive `*` / `?` patterns against directory
names at any depth. Preserved groups are still scanned and cleaned; they are not
flattened. `preserveWslFolders` also retains groups containing `wslg.exe` launchers.
Use `preservedFolders: []` and `preserveWslFolders: false` to flatten every
non-ignored group. Touched folders are removed only when truly empty, unless
`keepEmptyFolders` is true; `desktop.ini` counts as a remaining file.

Deduplication is independent in each selected menu. Matching checks launch target,
arguments, working directory, icon, window state, hotkey, flags, and AppUserModelID.
URL shortcut content must match. Different installations are never overwritten.
Shared WSL links are not repaired against one user's WSL registry. User WSL IDs
are repaired only when one registered distribution matches the shortcut name.
Wrapper scripts are inspected, never executed or decoded.

Legacy `ignoreFolders`, `ignores`, `removalTerms`, `useDefaultIgnoredFolders`, and
`useDefaultRemovalTerms` remain accepted. The legacy default-list switches can
explicitly add defaults to custom lists. `overwriteExistingShortcuts` is accepted
but cannot authorize overwriting an unrelated launcher.
