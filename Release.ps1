<#
.SYNOPSIS
    Publishes, packages, and (optionally) publishes a Velopack release of Vulcano Control.

.DESCRIPTION
    Produces a self-contained win-x64 build and packs it into a Velopack release
    (installer + update feed) under .\Releases. With -Publish, also uploads the result
    straight to a new GitHub Release.

    This packs under 'Vulcano-Control', the id the WPF app used to hold. During the rewrite the
    Avalonia app was published as 'Vulcano-Control-Preview' so that Velopack would treat it as a
    separate application and leave the working WPF install alone; with the WPF app retired, that
    separation has done its job and the rewrite takes the real id back. Anyone still on the WPF app
    sees this as an update to it, which is the intent.

    Releases packed under the old preview id are moved to .\Releases\preview-archive: vpk reads the
    feed in the output directory to work out what came before, and two ids in one folder is not a
    question it should have to answer.

    The rewrite does not check for updates yet; the installer and its shortcuts work regardless.

.PARAMETER Token
    GitHub Personal Access Token (scope: 'repo'), used only with -Publish. Defaults to the
    GITHUB_TOKEN environment variable. Create one at https://github.com/settings/tokens and
    store it once via:
      [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', '<token>', 'User')
    then restart PowerShell.

.EXAMPLE
    .\Release.ps1 -Version 2.0.0
    Packs locally only.

.EXAMPLE
    .\Release.ps1 -Version 2.0.0 -Publish
    Packs and publishes it as a GitHub Release (tag v2.0.0).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$Publish,

    [string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$releasesDir = Join-Path $root "Releases"
$publishDir = Join-Path $root "publish"
$repoUrl = "https://github.com/Senifox/Vulcano-Control"

$project = Join-Path $root "src\Vulcano.App\Vulcano.App.csproj"
$packId = "Vulcano-Control"
$packTitle = "Vulcano Control"
$mainExe = "vulcano-control.exe"
$icon = Join-Path $root "src\Vulcano.App\Assets\Icons\vulcano-control.ico"

Write-Host "Building $packId v$Version"

# Anything left over from the preview id would be read as this app's history, which it is not.
$strays = Get-ChildItem $releasesDir -File -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -like "Vulcano-Control-Preview*" }
if ($strays) {
    $archive = Join-Path $releasesDir "preview-archive"
    New-Item -ItemType Directory -Force $archive | Out-Null
    $strays | ForEach-Object { Move-Item $_.FullName (Join-Path $archive $_.Name) -Force }

    # The feed files name the packages they describe, so they belong with them.
    Get-ChildItem $releasesDir -File | Where-Object { $_.Name -in @("RELEASES", "releases.win.json", "assets.win.json") } |
        ForEach-Object { Move-Item $_.FullName (Join-Path $archive $_.Name) -Force }

    Write-Host "Moved $($strays.Count) package(s) from the preview id into: $archive"
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir

vpk pack `
    --packId $packId `
    --packTitle $packTitle `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe $mainExe `
    --icon $icon `
    --outputDir $releasesDir

Write-Host ""
Write-Host "Paket erstellt in: $releasesDir"

if (-not $Publish) {
    Write-Host "Naechster Schritt: entweder mit '-Publish' erneut ausfuehren, um direkt als GitHub Release zu"
    Write-Host "veroeffentlichen, oder die Dateien aus '$releasesDir' manuell als Assets an ein neues Release (Tag v$Version) haengen."
    return
}

if (-not $Token) {
    throw "Kein GitHub-Token gefunden. Einmalig ein Personal Access Token (Scope 'repo') unter " + `
        "https://github.com/settings/tokens erzeugen und als Umgebungsvariable hinterlegen: " + `
        "[Environment]::SetEnvironmentVariable('GITHUB_TOKEN', '<token>', 'User'), danach PowerShell neu starten."
}

$prerelease = if ($Version -match "-") { "true" } else { "false" }

vpk upload github `
    --repoUrl $repoUrl `
    --token $Token `
    --outputDir $releasesDir `
    --tag "v$Version" `
    --publish true `
    --pre $prerelease

Write-Host ""
Write-Host "Release v$Version veroeffentlicht: $repoUrl/releases/tag/v$Version"
