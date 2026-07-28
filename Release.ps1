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
    Packs and publishes it as a GitHub Release (tag v2.0.0), with Cleanup.ps1 attached.

.EXAMPLE
    .\Release.ps1 -Version 2.0.0 -AttachOnly
    Builds nothing. Only puts the extra assets on the release that is already out.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$Publish,

    # Skips building and packing and only attaches the extra assets to a release that is already
    # out. For fixing up a release, and for exercising that path without cutting a new one.
    [switch]$AttachOnly,

    # How many full packages to keep in .\Releases after packing. One is what a delta needs; the
    # second is insurance. Set higher to keep more history locally - every version is on its
    # GitHub release regardless.
    [int]$KeepPackages = 2,

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

if ($AttachOnly) {
    $Publish = $true
}
else {
    Write-Host "Building $packId v$Version"
}

# Anything left over from the preview id would be read as this app's history, which it is not.
$strays = if ($AttachOnly) { $null } else {
    Get-ChildItem $releasesDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "Vulcano-Control-Preview*" }
}
if ($strays) {
    $archive = Join-Path $releasesDir "preview-archive"
    New-Item -ItemType Directory -Force $archive | Out-Null
    $strays | ForEach-Object { Move-Item $_.FullName (Join-Path $archive $_.Name) -Force }

    # The feed files name the packages they describe, so they belong with them.
    Get-ChildItem $releasesDir -File | Where-Object { $_.Name -in @("RELEASES", "releases.win.json", "assets.win.json") } |
        ForEach-Object { Move-Item $_.FullName (Join-Path $archive $_.Name) -Force }

    Write-Host "Moved $($strays.Count) package(s) from the preview id into: $archive"
}

<#
    Drops packages older than the last few.

    vpk builds one delta, against the newest full package, so that is the only one a future release
    needs. The rest accumulate at 54 MB a version and are already on the GitHub release they belong
    to - keeping them locally is keeping a second copy of something nobody reads.

    Two are kept rather than one: the newest is what the next delta is built from, and the one
    behind it costs little and means a release that has to be re-cut still has something to fall
    back on. Deltas are matched to the fulls they were built alongside.

    Called before packing, not after. vpk writes the feed from whatever is in the directory, so
    pruning afterwards leaves the feed naming packages that are no longer there - and that feed is
    what goes up with the release. It happened once, in 2.4.1, which lists a 2.3.0 that is not among
    its assets.
#>
function Remove-SupersededPackages {
    $fulls = Get-ChildItem $releasesDir -Filter "*-full.nupkg" -File -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending

    if ($fulls.Count -le $KeepPackages) { return }

    $doomed = $fulls | Select-Object -Skip $KeepPackages
    $freed = 0

    foreach ($full in $doomed) {
        $version = $full.Name -replace "^$packId-", "" -replace "-full\.nupkg$", ""
        foreach ($file in Get-ChildItem $releasesDir -Filter "$packId-$version-*.nupkg" -File) {
            $freed += $file.Length
            Remove-Item $file.FullName -Force
        }
    }

    Write-Host ("Removed {0} superseded package(s), {1:N0} MB - they are on their GitHub release." -f `
        $doomed.Count, ($freed / 1MB))
}

if (-not $AttachOnly) {
    Remove-SupersededPackages

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

}

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

if (-not $AttachOnly) {
    vpk upload github `
        --repoUrl $repoUrl `
        --token $Token `
        --outputDir $releasesDir `
        --tag "v$Version" `
        --publish true `
        --pre $prerelease
}

<#
    Attaches a file to a release that already exists.

    vpk uploads the packages it built and nothing else, so anything that has to travel with a
    release goes up separately through the API. Re-uploading replaces what is there, because a
    release published twice should end up with one copy and not fail on the second run.
#>
function Add-ReleaseAsset([string]$tag, [string]$file) {
    $name = Split-Path $file -Leaf
    $slug = ($repoUrl -replace "^https://github\.com/", "")
    $headers = @{ Authorization = "Bearer $Token"; "User-Agent" = "vulcano-release" }

    $release = Invoke-RestMethod "https://api.github.com/repos/$slug/releases/tags/$tag" -Headers $headers

    $existing = $release.assets | Where-Object { $_.name -eq $name }
    if ($existing) {
        Invoke-RestMethod "https://api.github.com/repos/$slug/releases/assets/$($existing.id)" `
            -Headers $headers -Method Delete | Out-Null
    }

    Invoke-RestMethod "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$name" `
        -Headers $headers -Method Post `
        -ContentType "text/plain" -InFile $file | Out-Null

    Write-Host "  attached: $name"
}

<#
    Puts this version's changelog section into the release description.

    The same file the app reads, so what somebody is told on GitHub and what the app shows them
    afterwards cannot drift apart. A version with no section is left with an empty description
    rather than a made-up one - a release note that says nothing is better than one that is wrong.
#>
function Set-ReleaseNotes([string]$tag, [string]$version) {
    $changelog = Join-Path $root "CHANGELOG.md"
    if (-not (Test-Path $changelog)) { return }

    $lines = Get-Content $changelog
    $notes = @()
    $inSection = $false

    foreach ($line in $lines) {
        if ($line -match '^##\s+(\S+)') {
            # The next version's heading ends this one.
            if ($inSection) { break }
            $inSection = ($Matches[1] -eq $version)
            continue
        }

        if ($inSection) { $notes += $line }
    }

    $body = ($notes -join "`n").Trim()
    if (-not $body) {
        Write-Host "  no changelog section for $version - description left alone"
        return
    }

    $slug = ($repoUrl -replace "^https://github\.com/", "")
    $headers = @{ Authorization = "Bearer $Token"; "User-Agent" = "vulcano-release" }
    $release = Invoke-RestMethod "https://api.github.com/repos/$slug/releases/tags/$tag" -Headers $headers

    Invoke-RestMethod "https://api.github.com/repos/$slug/releases/$($release.id)" `
        -Headers $headers -Method Patch `
        -ContentType "application/json" `
        -Body (@{ body = $body } | ConvertTo-Json) | Out-Null

    Write-Host "  description set from CHANGELOG.md"
}

Write-Host ""
Write-Host "Extra assets..."

# Anyone still on a preview build from the rewrite has to run this before installing: the preview
# is a separate application to the installer, and its settings sit in the folder this installer
# clears. Shipping it with the release is the difference between finding it and losing the profiles.
Add-ReleaseAsset "v$Version" (Join-Path $root "Cleanup.ps1")

Set-ReleaseNotes "v$Version" $Version

Write-Host ""
Write-Host "Release v$Version veroeffentlicht: $repoUrl/releases/tag/v$Version"
