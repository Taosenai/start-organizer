# Release checklist

This repository does not yet have a selected license. **Choose and add `LICENSE`
before public distribution.** The release-readiness PR intentionally leaves that
owner decision open rather than selecting terms on the owner's behalf.

1. Review the changes and pass the Windows CI job. Decide whether the existing
   commit author name/email should be public. This PR does not rewrite history;
   a complete historical secret scan is a separate pre-publication check.
2. Run the following from a clean Windows checkout with the .NET 10 SDK and
   PowerShell 7. The license requirement prevents accidentally treating an
   unlicensed CI test artifact as a ready-to-share release.

   ```powershell
   pwsh -File .\scripts\Build-Release.ps1 -RequireLicense
   ```

3. On a second Windows account or disposable machine, review a dry run, apply, and
   undo. Exercise user-only mode and elevation with both menus. Verify that a
   shared launcher remains available to the other account. Keep application
   installers/updaters closed. Do not use a production Start Menu as a test fixture.
4. Test the extracted ZIP on a Windows x64 machine without a separate .NET runtime.
   CI already tests the ZIP in an isolated directory and verifies self-contained
   startup using the host trace, but is not a substitute for this clean-machine
   check or SmartScreen behavior for an unsigned download.
5. Tag the reviewed commit as `v0.1.1` and create a new release containing the ZIP
   and `SHA256SUMS.txt` from `artifacts`. Do not replace the existing `v0.1.0` assets.
   Include the license in the package. Nothing in CI publishes or changes visibility.

The release script runs the custom test executable (not `dotnet test`), publishes
a self-contained single executable with native runtime libraries included,
packages only the executable/docs/license, and tests `--help`, `--version`, and
invalid explicit configuration against the extracted ZIP. The host trace must
confirm a self-contained single-file app using its internal host policy; the
.NET 10 Windows host embeds CoreCLR directly, so extracting `coreclr.dll` is not
required. Config files and backups are never included in the ZIP.

The binaries are not code-signed. The source path mapping removes local build
paths from embedded symbols; this is not a full binary/history privacy audit.
The GitHub Actions job has read-only repository permissions and produces test
artifacts only. License selection, history cleanup, visibility, tagging, and
publishing remain manual owner decisions.
