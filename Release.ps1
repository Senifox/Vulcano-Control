<#
.SYNOPSIS
    Publishes, packages, and (optionally) publishes a Velopack release of Vulcano Control.

.DESCRIPTION
    Produces a self-contained win-x64 build and packs it into a Velopack release
    (installer + update feed) under .\Releases. With -Publish, also uploads the result
    straight to a new GitHub Release.

    Two apps live in this repository while the rewrite is finished:

      -App avalonia  (default)  the Avalonia rewrite, packId Vulcano-Control-Preview
      -App wpf                  the original WPF app, packId Vulcano-Control

    The pack ids are deliberately different. Velopack treats a package with the same id as an
    update of an existing install, so publishing the rewrite under 'Vulcano-Control' would replace
    the WPF app on everyone's machine - including on this one, where it is still the app that can
    actually talk to a Volcano. At the cutover the rewrite takes over the 'Vulcano-Control' id and
    the preview id is retired.

    The rewrite does not check for updates yet; the installer and its shortcuts work regardless.

.PARAMETER Token
    GitHub Personal Access Token (scope: 'repo'), used only with -Publish. Defaults to the
    GITHUB_TOKEN environment variable. Create one at https://github.com/settings/tokens and
    store it once via:
      [Environment]::SetEnvironmentVariable('GITHUB_TOKEN', '<token>', 'User')
    then restart PowerShell.

.EXAMPLE
    .\Release.ps1 -Version 2.0.0-preview.1
    Packs the rewrite locally only.

.EXAMPLE
    .\Release.ps1 -App wpf -Version 1.5.1 -Publish
    Packs the WPF app and publishes it as a GitHub Release (tag v1.5.1).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("avalonia", "wpf")]
    [string]$App = "avalonia",

    [switch]$Publish,

    [string]$Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$releasesDir = Join-Path $root "Releases"
$publishDir = Join-Path $root "publish"
$repoUrl = "https://github.com/Senifox/Vulcano-Control"

$settings = switch ($App) {
    "avalonia" {
        @{
            Project = Join-Path $root "src\Vulcano.App\Vulcano.App.csproj"
            PackId  = "Vulcano-Control-Preview"
            Title   = "Vulcano Control (Preview)"
            MainExe = "vulcano-control.exe"
            Icon    = Join-Path $root "src\Vulcano.App\Assets\Icons\vulcano-control.ico"
        }
    }
    "wpf" {
        @{
            Project = Join-Path $root "Vulcano-Control\Vulcano-Control.csproj"
            PackId  = "Vulcano-Control"
            Title   = "Vulcano Control"
            MainExe = "Vulcano-Control.exe"
            Icon    = Join-Path $root "Vulcano-Control\Icon.ico"
        }
    }
}

Write-Host "Building $App as $($settings.PackId) v$Version"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $settings.Project -c Release -r win-x64 --self-contained true -o $publishDir

vpk pack `
    --packId $settings.PackId `
    --packTitle $settings.Title `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe $settings.MainExe `
    --icon $settings.Icon `
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
