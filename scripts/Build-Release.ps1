#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$RequireLicense
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false # We check every native exit code, including intentional failures.
if (-not $IsWindows) { throw 'Build and shortcut tests require Windows.' }

$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$license = Join-Path $repo 'LICENSE'
if ($RequireLicense -and -not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'Choose and add LICENSE before preparing a public release.'
}
$project = Join-Path $repo 'StartMenuOrganizer.csproj'
$version = [string]([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected project version: $version" }
$work = Join-Path ([IO.Path]::GetTempPath()) ('start-organizer-release-' + [guid]::NewGuid().ToString('N'))
$environment = @{}
foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'DOTNET_HOST_TRACE', 'DOTNET_HOST_TRACEFILE', 'DOTNET_HOST_TRACE_VERBOSITY')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

Push-Location $repo
try {
    New-Item -ItemType Directory -Path $work, $output -Force | Out-Null
    Invoke-DotNet -Arguments @('build', $project, '-c', 'Release')
    Invoke-DotNet -Arguments @('run', '--project', (Join-Path $repo 'tests\StartOrganizer.Tests.csproj'), '-c', 'Release')

    $publish = Join-Path $work 'publish'
    Invoke-DotNet -Arguments @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish)
    if (@(Get-ChildItem -LiteralPath $publish -Filter '*.dll' -Force).Count -ne 0) {
        throw 'Publish left loose DLLs; the single-file package would be incomplete.'
    }
    $stage = Join-Path $work 'stage'
    New-Item -ItemType Directory -Path (Join-Path $stage 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'start-organizer.exe'), (Join-Path $repo 'README.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repo 'docs\configuration.md'), (Join-Path $repo 'docs\releasing.md') -Destination (Join-Path $stage 'docs')
    if (Test-Path -LiteralPath $license -PathType Leaf) { Copy-Item -LiteralPath $license -Destination $stage }
    else { Write-Warning 'No LICENSE selected: this package is a test artifact, not ready for public distribution.' }

    $zip = Join-Path $output "start-organizer-v$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    $extracted = Join-Path $work 'extracted'
    Expand-Archive -LiteralPath $zip -DestinationPath $extracted
    $exe = Join-Path $extracted 'start-organizer.exe'
    $smoke = Join-Path $work 'unrelated-working-directory'
    $emptyRuntime = Join-Path $work 'empty-dotnet-root'
    $bundle = Join-Path $work 'native-bundle'
    New-Item -ItemType Directory -Path $smoke, $emptyRuntime, $bundle | Out-Null
    $env:DOTNET_ROOT = $emptyRuntime
    $env:DOTNET_ROOT_X64 = $emptyRuntime
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundle
    $env:DOTNET_HOST_TRACE = '1'
    $env:DOTNET_HOST_TRACEFILE = Join-Path $work 'host-trace.txt'
    $env:DOTNET_HOST_TRACE_VERBOSITY = '4'
    Set-Location $smoke

    $help = (& $exe --help) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $help -notmatch 'Usage:') { throw 'Extracted executable help smoke test failed.' }
    $reportedVersion = (& $exe --version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $reportedVersion.Trim() -ne $version) { throw 'Extracted executable version smoke test failed.' }
    if (@(Get-ChildItem -LiteralPath $bundle -Filter 'coreclr.dll' -Recurse -Force).Count -eq 0) {
        Write-Host 'Native bundle diagnostic listing:'
        Get-ChildItem -LiteralPath $work -Recurse -Force | Select-Object FullName, Attributes, Length | Format-Table -AutoSize | Out-Host
        if (Test-Path -LiteralPath $env:DOTNET_HOST_TRACEFILE) {
            Get-Content -LiteralPath $env:DOTNET_HOST_TRACEFILE | Select-String -Pattern 'bundle|extract|coreclr|self-contained|framework|hostpolicy' | Select-Object -First 80 | Out-Host
        }
        throw 'The extracted executable did not extract its bundled native .NET runtime.'
    }
    if (Test-Path -LiteralPath (Join-Path $smoke 'start-organizer.config.json')) {
        throw 'Informational CLI options unexpectedly created a config.'
    }
    $badConfig = Join-Path $smoke 'bad config.json'
    Set-Content -LiteralPath $badConfig -Value '{"includeGobal":false}' -Encoding utf8NoBOM
    $failure = (& $exe --config $badConfig --apply 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 1 -or $failure -notmatch 'includeGobal' -or $failure -match 'Applying Start Menu') {
        throw 'Invalid-config CLI smoke test failed; no menu scan or apply should occur.'
    }
    $missing = Join-Path $smoke 'missing.json'
    $failure = (& $exe --config $missing 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $missing)) { throw 'Missing explicit config was accepted or created.' }
    if (Test-Path -LiteralPath (Join-Path $extracted 'start-organizer.config.json')) { throw 'Package unexpectedly contains a config.' }

    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Value "$hash  $([IO.Path]::GetFileName($zip))" -Encoding ascii
    Write-Host "Validated package: $zip"
}
finally {
    Pop-Location
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
# Intentional negative CLI tests leave LASTEXITCODE=1; the validated script succeeded.
exit 0
