<#
.SYNOPSIS
    Removes the old WPF installation and the notification identities the Avalonia app registered.

.DESCRIPTION
    Two apps and one data folder live side by side during the rewrite, and the awkward part is that
    they share it: the WPF app installs into %LocalAppData%\Vulcano-Control, and that same folder
    holds settings.json, settings.v2.json, the logs and the measurements. An uninstaller pointed at
    the folder would take the settings and every saved ramp profile with it.

    So this backs the data up before touching anything, and puts it back afterwards if the
    uninstaller cleared the folder out. Nothing is deleted without that copy existing first.

    It reports and changes nothing unless -Execute is given. Read the report, then run it again.

.PARAMETER Execute
    Actually do it. Without this the script only says what it found and what it would do.

.PARAMETER IncludePreview
    Also remove the Avalonia preview install (Vulcano-Control-Preview). Off by default, because
    while the rewrite is being tested that is the app in use.

.PARAMETER BackupTo
    Where to copy the settings and logs first. Defaults to a timestamped folder on the desktop.

.EXAMPLE
    .\Cleanup.ps1
    Says what is installed and what would go.

.EXAMPLE
    .\Cleanup.ps1 -Execute
    Removes the WPF install and the stale notification identities, keeps the preview and all data.
#>
param(
    [switch]$Execute,
    [switch]$IncludePreview,
    [string]$BackupTo,

    # Where the two installs live, and where the notification identities are registered. All three
    # are overridable so this script can be rehearsed somewhere harmless: it deletes things, and a
    # script that deletes things should be runnable against a copy first.
    #
    # All three, because a rehearsal that overrides only the folders still reaches into the real
    # registry - which is exactly what happened the first time this was tried, and it removed the
    # identity of an app that was staying.
    [string]$DataDirectory,
    [string]$PreviewDirectory,
    [string]$IdentityRoot = "HKCU:\Software\Classes\AppUserModelId"
)

$ErrorActionPreference = "Stop"

if (-not $DataDirectory) { $DataDirectory = Join-Path $env:LOCALAPPDATA "Vulcano-Control" }
if (-not $PreviewDirectory) { $PreviewDirectory = Join-Path $env:LOCALAPPDATA "Vulcano-Control-Preview" }

$dataDirectory = $DataDirectory
$previewDirectory = $PreviewDirectory
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"

# Everything here is the user's, not the app's. It lives in the same folder as the WPF install by
# design - the rewrite reads the old app's settings - which is exactly why it has to be carried out
# of the way before an uninstaller runs.
$dataFiles = @("settings.json", "settings.v2.json", "vulcano-control.log")
$dataFolders = @("measurements")

# The app wrote this for itself so a toast could show an icon. Backed up like the rest, but it is
# not the user's and it goes when the identity it belongs to goes.
$iconFile = "notification-icon.png"

# Velopack derives the id on the Start menu shortcut from the pack id, with this prefix. An identity
# belongs to an install, so each one is only removed when its install is going or already gone -
# taking the identity out from under an app that is staying would just break its notifications.
$identityPrefix = "$IdentityRoot\velopack."
$strayIdentity = "$IdentityRoot\vulcano-control"

function Write-Finding([string]$what, [string]$state, [string]$detail = "") {
    $mark = switch ($state) { "found" { "[x]" } "missing" { "[ ]" } default { "[!]" } }
    Write-Host ("  {0} {1,-46} {2}" -f $mark, $what, $detail)
}

<#
    Which app is installed in a folder. Both are called vulcano-control.exe, so the executable name
    settles nothing; the rewrite is the one that brings Vulcano.Core.dll along. This matters because
    after the cutover the Avalonia app takes over the Vulcano-Control id, and this script must not
    then uninstall the app it was written to make room for.
#>
function Get-InstalledApp([string]$root) {
    if (-not (Test-Path (Join-Path $root "Update.exe"))) { return $null }

    return [pscustomobject]@{
        Root      = $root
        IsRewrite = Test-Path (Join-Path $root "current\Vulcano.Core.dll")
        Update    = Join-Path $root "Update.exe"
    }
}

function Stop-IfRunning {
    $running = Get-Process vulcano-control, Vulcano-Control -ErrorAction SilentlyContinue
    if ($running) {
        throw "Vulcano Control is running (pid $(($running.Id) -join ', ')). Close it and run this again."
    }
}

function Backup-Data([string]$destination) {
    New-Item -ItemType Directory -Force $destination | Out-Null

    foreach ($name in $dataFiles) {
        $source = Join-Path $dataDirectory $name
        if (Test-Path $source) { Copy-Item $source (Join-Path $destination $name) -Force }
    }

    foreach ($name in $dataFolders) {
        $source = Join-Path $dataDirectory $name
        if (Test-Path $source) { Copy-Item $source (Join-Path $destination $name) -Recurse -Force }
    }

    $icon = Join-Path $dataDirectory $iconFile
    if (Test-Path $icon) { Copy-Item $icon (Join-Path $destination $iconFile) -Force }

    # The exported logs are named by date, so they are matched rather than listed.
    Get-ChildItem $dataDirectory -Filter "vulcano-control-log-*.txt" -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $destination $_.Name) -Force }
}

function Restore-Data([string]$backup) {
    if (-not (Test-Path $backup)) { return }

    New-Item -ItemType Directory -Force $dataDirectory | Out-Null

    Get-ChildItem $backup | ForEach-Object {
        $target = Join-Path $dataDirectory $_.Name
        if (-not (Test-Path $target)) {
            Copy-Item $_.FullName $target -Recurse -Force
            Write-Host "      put back: $($_.Name)"
        }
    }
}

# --- Look ---

Write-Host ""
Write-Host "Vulcano Control cleanup"
Write-Host ""
Write-Host "Installed:"

$wpf = Get-InstalledApp $dataDirectory
$preview = Get-InstalledApp $previewDirectory

if ($null -eq $wpf) {
    Write-Finding "WPF app in Vulcano-Control" "missing"
}
elseif ($wpf.IsRewrite) {
    Write-Finding "Vulcano-Control" "warn" "holds the REWRITE, not the WPF app - left alone"
}
else {
    Write-Finding "WPF app in Vulcano-Control" "found" "will be uninstalled"
}

if ($null -eq $preview) {
    Write-Finding "preview in Vulcano-Control-Preview" "missing"
}
elseif ($IncludePreview) {
    Write-Finding "preview in Vulcano-Control-Preview" "found" "will be uninstalled"
}
else {
    Write-Finding "preview in Vulcano-Control-Preview" "found" "kept (pass -IncludePreview to remove)"
}

# An install that is staying keeps its identity; one that is going, or was never there, does not.
$wpfGoing = $null -ne $wpf -and -not $wpf.IsRewrite
$previewGoing = $null -ne $preview -and $IncludePreview

$identities = @(
    @{ Key = "${identityPrefix}Vulcano-Control";         Doomed = ($null -eq $wpf) -or $wpfGoing }
    @{ Key = "${identityPrefix}Vulcano-Control-Preview"; Doomed = ($null -eq $preview) -or $previewGoing }
    @{ Key = $strayIdentity;                             Doomed = $true }
)

Write-Host ""
Write-Host "Notification identities:"
foreach ($identity in $identities) {
    $name = $identity.Key.Split('\')[-1]
    if (-not (Test-Path $identity.Key)) { Write-Finding $name "missing"; continue }

    Write-Finding $name "found" $(if ($identity.Doomed) { "will be removed" } else { "kept - its app is staying" })
}

Write-Host ""
Write-Host "Kept, always - this is yours, not the app's:"
foreach ($name in ($dataFiles + $dataFolders)) {
    $path = Join-Path $dataDirectory $name
    if (Test-Path $path) { Write-Finding $name "found" "backed up, then put back" }
}

if (-not $Execute) {
    Write-Host ""
    Write-Host "Nothing was changed. Run again with -Execute to carry this out."
    Write-Host "Add -IncludePreview to remove the preview install as well."
    Write-Host ""
    return
}

# --- Act ---

Stop-IfRunning

if (-not $BackupTo) {
    $stamp = Get-Date -Format "yyyy-MM-dd-HHmmss"
    $BackupTo = Join-Path ([Environment]::GetFolderPath("Desktop")) "vulcano-control-backup-$stamp"
}

Write-Host ""
Write-Host "Backing up settings and logs to:"
Write-Host "  $BackupTo"
Backup-Data $BackupTo

$removed = @()

if ($null -ne $wpf -and -not $wpf.IsRewrite) {
    Write-Host ""
    Write-Host "Uninstalling the WPF app..."
    & $wpf.Update "--uninstall" "--silent"
    if ($LASTEXITCODE -ne 0) { Write-Host "  the uninstaller reported exit code $LASTEXITCODE" }
    $removed += "WPF app"

    # Velopack may clear the whole folder, settings included. That is what the backup was for.
    Restore-Data $BackupTo
}

if ($null -ne $preview -and $IncludePreview) {
    Write-Host ""
    Write-Host "Uninstalling the preview..."
    & $preview.Update "--uninstall" "--silent"
    if ($LASTEXITCODE -ne 0) { Write-Host "  the uninstaller reported exit code $LASTEXITCODE" }
    $removed += "preview"

    Restore-Data $BackupTo
}

Write-Host ""
Write-Host "Notification identities..."
foreach ($identity in $identities) {
    if (-not (Test-Path $identity.Key)) { continue }

    if (-not $identity.Doomed) {
        Write-Host "  kept: $($identity.Key.Split('\')[-1]) - its app is staying"
        continue
    }

    Remove-Item $identity.Key -Recurse -Force
    Write-Host "  removed: $($identity.Key.Split('\')[-1])"
}

# The icon exists only so a toast can show one. It goes when nothing is left to raise a toast, and
# stays as long as an app that might is still installed.
$anyIdentityLeft = $identities | Where-Object { Test-Path $_.Key }
$icon = Join-Path $dataDirectory $iconFile
if ((Test-Path $icon) -and -not $anyIdentityLeft) {
    Remove-Item $icon -Force
    Write-Host "  removed: $iconFile (a copy is in the backup)"
}

Write-Host ""
Write-Host "Left behind on purpose:"
foreach ($name in ($dataFiles + $dataFolders)) {
    $path = Join-Path $dataDirectory $name
    if (Test-Path $path) { Write-Host "  $name" }
}

Write-Host ""
Write-Host "Done. The backup stays where it is - delete it yourself once you are satisfied."
Write-Host ""
