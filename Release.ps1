<#
.SYNOPSIS
    Publishes, packages, and (optionally) publishes a Velopack release of Vulcano-Control.

.DESCRIPTION
    Produces a self-contained win-x64 build and packs it into a Velopack release
    (installer + update feed) under .\Releases. With -Publish, also uploads the result
    straight to a new GitHub Release so the app's built-in update check can find it -
    no manual upload needed.

.PARAMETER Token
    GitHub Personal Access Token (scope: 'repo'), used only with -Publish. Defaults to the
    GITHUB_TOKEN environment variable. Create one at https://github.com/settings/tokens and
    store it once via:
      [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', '<token>', 'User')
    then restart PowerShell.

.EXAMPLE
    .\Release.ps1 -Version 1.1.0
    Packs the release locally only.

.EXAMPLE
    .\Release.ps1 -Version 1.1.0 -Publish
    Packs the release and publishes it directly as a GitHub Release (tag v1.1.0).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$Publish,

    [string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "Vulcano-Control\Vulcano-Control.csproj"
$publishDir = Join-Path $root "publish"
$releasesDir = Join-Path $root "Releases"
$repoUrl = "https://github.com/Senifox/Vulcano-Control"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir

vpk pack `
    --packId Vulcano-Control `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe Vulcano-Control.exe `
    --icon (Join-Path $root "Vulcano-Control\Icon.ico") `
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
