# CheckDotNetLogs.ps1
# Checks every known log/dump/trace location for a .NET app crash.
# Usage: .\CommonScripts\CheckDotNetLogs.ps1 [-AppName AggressiveScreensaver] [-LogTail 50]

param(
    [string]$AppName = "AggressiveScreensaver",
    [int]   $LogTail = 50
)

$sep      = { param($title) Write-Host "`n=== $title ===" -ForegroundColor Cyan }
$none     = { Write-Host "  (none)" -ForegroundColor DarkGray }
$anyFound = $false

# ---------------------------------------------------------------------------
# 1. App log (%LOCALAPPDATA%\<AppName>\<AppName>.log)
# ---------------------------------------------------------------------------
& $sep "App Log"
$logPath = "$env:LOCALAPPDATA\$AppName\$AppName.log"
Write-Host "  Path: $logPath"
if (Test-Path $logPath) {
    $anyFound = $true
    Get-Content $logPath -Tail $LogTail
} else {
    & $none
}

# ---------------------------------------------------------------------------
# 2. WER auto-collected dumps (%LOCALAPPDATA%\CrashDumps)
#    Windows saves these automatically for every unhandled crash.
# ---------------------------------------------------------------------------
& $sep "WER Dumps (%LOCALAPPDATA%\CrashDumps)"
$dumps = Get-ChildItem "$env:LOCALAPPDATA\CrashDumps" -Filter "$AppName*.dmp" `
    -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
if ($dumps) {
    $anyFound = $true
    $dumps | Format-Table Name, LastWriteTime,
        @{n='KB'; e={[math]::Round($_.Length / 1KB, 1)}} -AutoSize

    $latest = $dumps[0]
    Write-Host "  --- Managed stack trace from: $($latest.Name) ---" -ForegroundColor Yellow
    if (Get-Command dotnet-dump -ErrorAction SilentlyContinue) {
        dotnet-dump analyze $latest.FullName --command "clrstack" 2>&1 | Select-Object -First 50
    } else {
        Write-Host "  dotnet-dump not installed. Run: dotnet tool install -g dotnet-dump" -ForegroundColor Yellow
    }
} else {
    & $none
}

# ---------------------------------------------------------------------------
# 3. WER Report Archive (post-analysis reports, may contain ETL/logs)
# ---------------------------------------------------------------------------
& $sep "WER Report Archive"
$werArchivePaths = @(
    "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportArchive",
    "$env:APPDATA\Microsoft\Windows\WER\ReportArchive"
)
$archiveFound = $false
foreach ($path in $werArchivePaths) {
    $reports = Get-ChildItem $path -Filter "*$AppName*" -ErrorAction SilentlyContinue `
        | Sort-Object LastWriteTime -Descending | Select-Object -First 5
    if ($reports) {
        $anyFound = $true
        $archiveFound = $true
        Write-Host "  $path"
        $reports | Format-Table Name, LastWriteTime -AutoSize
    }
}
if (-not $archiveFound) { & $none }

# ---------------------------------------------------------------------------
# 4. WER Report Queue (pending, not yet submitted to Microsoft)
# ---------------------------------------------------------------------------
& $sep "WER Report Queue (pending)"
$werQueuePaths = @(
    "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportQueue",
    "$env:APPDATA\Microsoft\Windows\WER\ReportQueue"
)
$queueFound = $false
foreach ($path in $werQueuePaths) {
    $reports = Get-ChildItem $path -Filter "*$AppName*" -ErrorAction SilentlyContinue
    if ($reports) {
        $anyFound = $true
        $queueFound = $true
        Write-Host "  $path"
        $reports | Format-Table Name, LastWriteTime -AutoSize
    }
}
if (-not $queueFound) { & $none }

# ---------------------------------------------------------------------------
# 5. Event Viewer - Application Error (native AV crash records)
# ---------------------------------------------------------------------------
& $sep "Event Viewer - Application Error"
try {
    $events = Get-WinEvent -FilterHashtable @{
        LogName      = 'Application'
        ProviderName = 'Application Error'
    } -MaxEvents 50 -ErrorAction Stop |
        Where-Object { $_.Message -like "*$AppName*" } |
        Select-Object -First 5

    if ($events) {
        $anyFound = $true
        $events | Format-List TimeCreated, Message
    } else {
        & $none
    }
} catch {
    Write-Host "  (could not query Event Viewer: $_)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 6. Event Viewer - .NET Runtime (managed unhandled exceptions with full stack)
# ---------------------------------------------------------------------------
& $sep "Event Viewer - .NET Runtime"
try {
    $events = Get-WinEvent -FilterHashtable @{
        LogName      = 'Application'
        ProviderName = '.NET Runtime'
    } -MaxEvents 50 -ErrorAction Stop |
        Where-Object { $_.Message -like "*$AppName*" } |
        Select-Object -First 5

    if ($events) {
        $anyFound = $true
        $events | Format-List TimeCreated, Message
    } else {
        & $none
    }
} catch {
    Write-Host "  (could not query Event Viewer: $_)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
if ($anyFound) {
    Write-Host "Crash data found. See above." -ForegroundColor Yellow
} else {
    Write-Host "No crash data found for '$AppName'." -ForegroundColor Green
}

