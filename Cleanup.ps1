<#
.SYNOPSIS
    Removes the old WPF installation and the notification identities the Avalonia app registered.

.DESCRIPTION
    Settings and ramp profiles used to live in %LocalAppData%\Vulcano-Control - the same folder the
    WPF app installs into, chosen so the rewrite would find the old app's settings rather than reset
    them. An uninstaller pointed at that folder takes the settings and every saved ramp profile with
    it, which is why this script used to back the folder up and put it back afterwards.

    The app now keeps its data in %AppData%\Vulcano-Control, where no installer ever reaches. So the
    job here is smaller and much safer: carry anything still sitting in the old folder across to the
    new one, back it up regardless, and only then uninstall.

    It reports and changes nothing unless -Execute is given. Read the report, then run it again.

.PARAMETER Execute
    Actually do it. Without this the script only says what it found and what it would do.

.PARAMETER IncludePreview
    Also remove the Avalonia preview install (Vulcano-Control-Preview). Off by default, because
    while the rewrite is being tested that is the app in use.

.PARAMETER BackupTo
    Where to copy the old settings and logs first. Defaults to a timestamped folder on the desktop.

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

    # Where the two installs live, where the data lives now, and where the notification identities
    # are registered. All four are overridable so this script can be rehearsed somewhere harmless:
    # it deletes things, and a script that deletes things should be runnable against a copy first.
    #
    # All four, because a rehearsal that overrides only the folders still reaches into the real
    # registry - which is exactly what happened the first time this was tried, and it removed the
    # identity of an app that was staying.
    [string]$InstallDirectory,
    [string]$PreviewDirectory,
    [string]$DataDirectory,
    [string]$IdentityRoot = "HKCU:\Software\Classes\AppUserModelId"
)

$ErrorActionPreference = "Stop"

if (-not $InstallDirectory) { $InstallDirectory = Join-Path $env:LOCALAPPDATA "Vulcano-Control" }
if (-not $PreviewDirectory) { $PreviewDirectory = Join-Path $env:LOCALAPPDATA "Vulcano-Control-Preview" }
if (-not $DataDirectory)    { $DataDirectory    = Join-Path $env:APPDATA "Vulcano-Control" }

$installDirectory = $InstallDirectory
$previewDirectory = $PreviewDirectory
$dataDirectory = $DataDirectory

<#
    The settings files, oldest home last. This is the same order the app itself looks in, and it has
    to be: whichever of these the app would have loaded is the one that has to end up in the new
    folder, or the first start after the cleanup comes up with somebody else's older profiles.
#>
$settingsChain = @("settings.v2.json", "settings.json")

# The rest of what is the user's, not the app's. The live log is not among them - it is written
# afresh on every start, and the one in the old folder is a record of runs that are over.
$dataFolders = @("measurements")
$exportedLogs = "vulcano-control-log-*.txt"

# Backed up like the rest, but app-owned: it exists only so a toast can show an icon, and the app
# writes it again in the new folder whenever it needs one.
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

<#
    Everything in the old folder that is worth keeping, as full paths. Used for both the report and
    the backup, so the two can never disagree about what is at stake.
#>
function Get-OldData {
    $items = @()
    if (-not (Test-Path $installDirectory)) { return $items }

    foreach ($name in ($settingsChain + $dataFolders + @($iconFile, "vulcano-control.log"))) {
        $path = Join-Path $installDirectory $name
        if (Test-Path $path) { $items += Get-Item $path }
    }

    # The exported logs are named by date, so they are matched rather than listed.
    $items += Get-ChildItem $installDirectory -Filter $exportedLogs -ErrorAction SilentlyContinue

    return $items
}

function Backup-Data([string]$destination) {
    New-Item -ItemType Directory -Force $destination | Out-Null

    foreach ($item in Get-OldData) {
        Copy-Item $item.FullName (Join-Path $destination $item.Name) -Recurse -Force
    }
}

<#
    Moves what is left in the old folder to the new one, which is the same thing the app does on its
    first start - done here as well because the uninstaller may get there first, and after that
    there is nothing left to migrate.

    Nothing already in the new folder is overwritten: what the app has been writing is by definition
    newer than what an install it has replaced left behind.
#>
function Copy-DataForward {
    if (-not (Test-Path $installDirectory)) { return }
    New-Item -ItemType Directory -Force $dataDirectory | Out-Null

    $settingsTarget = Join-Path $dataDirectory "settings.json"
    if (-not (Test-Path $settingsTarget)) {
        foreach ($name in $settingsChain) {
            $source = Join-Path $installDirectory $name
            if (-not (Test-Path $source)) { continue }

            Copy-Item $source $settingsTarget -Force
            Write-Host "      carried across: $name -> settings.json"
            break
        }
    }

    foreach ($name in $dataFolders) {
        $source = Join-Path $installDirectory $name
        $target = Join-Path $dataDirectory $name
        if ((Test-Path $source) -and -not (Test-Path $target)) {
            Copy-Item $source $target -Recurse -Force
            Write-Host "      carried across: $name"
        }
    }

    foreach ($log in Get-ChildItem $installDirectory -Filter $exportedLogs -ErrorAction SilentlyContinue) {
        $target = Join-Path $dataDirectory $log.Name
        if (-not (Test-Path $target)) {
            Copy-Item $log.FullName $target -Force
            Write-Host "      carried across: $($log.Name)"
        }
    }
}

<#
    Velopack's uninstaller detaches: Update.exe returns at once - with no exit code to read - and a
    separate process clears the folder afterwards. Anything that has to happen after the folder is
    gone therefore has to wait for it, rather than for Update.exe.
#>
function Wait-ForUninstall([string]$root, [int]$timeoutSeconds = 120) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path (Join-Path $root "Update.exe"))) {
            # It has stopped; give the last handful of deletions a moment to finish.
            Start-Sleep -Seconds 3
            return $true
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

# --- Look ---

Write-Host ""
Write-Host "Vulcano Control cleanup"
Write-Host ""
Write-Host "Installed:"

$wpf = Get-InstalledApp $installDirectory
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
Write-Host "Your data lives here now, and nothing below touches it:"
Write-Host "  $dataDirectory"

$oldData = Get-OldData
if ($oldData) {
    Write-Host ""
    Write-Host "Still in the old folder - backed up, then carried across before anything is removed:"
    foreach ($item in $oldData) { Write-Finding $item.Name "found" }
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

if ($oldData) {
    if (-not $BackupTo) {
        $stamp = Get-Date -Format "yyyy-MM-dd-HHmmss"
        $BackupTo = Join-Path ([Environment]::GetFolderPath("Desktop")) "vulcano-control-backup-$stamp"
    }

    Write-Host ""
    Write-Host "Backing up the old folder's settings and logs to:"
    Write-Host "  $BackupTo"
    Backup-Data $BackupTo

    Write-Host ""
    Write-Host "Carrying anything still needed across to $dataDirectory"
    Copy-DataForward
}

$removed = @()

if ($null -ne $wpf -and -not $wpf.IsRewrite) {
    Write-Host ""
    Write-Host "Uninstalling the WPF app..."
    & $wpf.Update "--uninstall" "--silent"

    if (Wait-ForUninstall $wpf.Root) {
        $removed += "WPF app"
    }
    else {
        Write-Host "  it is still going after two minutes - check the folder yourself afterwards."
    }
}

if ($null -ne $preview -and $IncludePreview) {
    Write-Host ""
    Write-Host "Uninstalling the preview..."
    & $preview.Update "--uninstall" "--silent"

    if (-not (Wait-ForUninstall $preview.Root)) {
        Write-Host "  it is still going after two minutes."
    }

    $removed += "preview"
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
if (-not $anyIdentityLeft) {
    foreach ($icon in @((Join-Path $dataDirectory $iconFile), (Join-Path $installDirectory $iconFile))) {
        if (Test-Path $icon) {
            Remove-Item $icon -Force
            Write-Host "  removed: $iconFile"
        }
    }
}

Write-Host ""
Write-Host "Kept, as always - this is yours, not the app's:"
foreach ($item in Get-ChildItem $dataDirectory -ErrorAction SilentlyContinue) {
    Write-Host "  $($item.Name)"
}

if ($BackupTo) {
    Write-Host ""
    Write-Host "The backup stays where it is - delete it yourself once you are satisfied:"
    Write-Host "  $BackupTo"
}

Write-Host ""
Write-Host "Done."
Write-Host ""
