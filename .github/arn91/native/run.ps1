param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SqlApp)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type -Path "$PSScriptRoot/Probe.cs"
Add-Type -Path "$PSScriptRoot/Native.cs"
Add-Type -Path "$PSScriptRoot/IdleNative.cs"
. "$PSScriptRoot/window.ps1"

$local = [Environment]::GetFolderPath('LocalApplicationData')
$data = Join-Path $local ScreenTimeTracker
$db = Join-Path $data screentime.db
if (!$env:GITHUB_ACTIONS -or !$env:RUNNER_TEMP -or ![Environment]::UserInteractive -or
    (Test-Path $data) -or (Test-Path (Join-Path $local Screeny)) -or (Get-Process Screeny -ErrorAction SilentlyContinue)) {
    throw 'Native probe requires a fresh disposable hosted interactive Windows profile; no data accessed'
}
function Wait-For([scriptblock]$condition, [string]$what, [int]$ms = 8000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 110
    } while ($timer.ElapsedMilliseconds -lt $ms)
    throw "Did not observe $what"
}
function Synthetic([string]$name) {
    Start-Process (Join-Path $env:RUNNER_TEMP "ScreenyTimeline$name.exe") -PassThru
}
function Rows {
    if (!(Test-Path $db)) { return }
    $values = & dotnet $SqlApp --native-rows $db
    if ($LASTEXITCODE -ne 0) { throw 'Read-only SQLite row query failed' }
    foreach ($line in $values) { ConvertFrom-Json $line }
}

$app = $alpha = $bravo = $null
try {
    $app = Start-Process $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
    $window = Wait-For { $app.Refresh(); if ($app.MainWindowHandle) { $app.MainWindowHandle } } 'Screeny HWND' 18000
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    if ($root.Current.ProcessId -ne $app.Id) { throw 'Application window ownership mismatch' }
    $ae = [Windows.Automation.AutomationElement]
    $button = Wait-For {
        $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new($ae::AutomationIdProperty, 'TrackingStatusButton'))
    } 'native tracking control'

    $alpha = Synthetic Alpha
    [void](Wait-For { [WindowProbe]::ForegroundPid() -eq $alpha.Id } 'synthetic Alpha foreground')
    if (![WindowProbe]::RefreshSyntheticInput($alpha.Id)) { throw 'Alpha synthetic input not sent' }
    $inputTick = [IdleNative]::Tick()
    if ([IdleNative]::Seconds() -gt 3) { throw 'Expected a fresh native input sample' }
    Write-Host 'Native Alpha foreground established; waiting for actual Windows input-idle threshold without changing clock, policy, or power.'
    $idleCutoff = Wait-For {
        if ([IdleNative]::Tick() -ne $inputTick) { throw 'Other input arrived while waiting for native idle; idle not proven' }
        if ([WindowProbe]::ForegroundPid() -ne $alpha.Id) { throw 'Unexpected focus change before idle; no input sent' }
        if ([IdleNative]::Seconds() -lt 301) { Start-Sleep -Milliseconds 850; return }
        $a = @(Rows | Where-Object { $_.process -eq 'screenytimelinealpha' })
        if ($a.Count -ge 1 -and ($a | Measure-Object -Property duration -Sum).Sum -ge 295000) { $a[-1] }
    } 'Alpha finalized by real native input idle' 325000
    Write-Host "Alpha was finalized at real idle ($([IdleNative]::Seconds())s idle; last duration $($idleCutoff.duration)ms)."

    $bravo = Synthetic Bravo
    [void](Wait-For { [WindowProbe]::ForegroundPid() -eq $bravo.Id } 'programmatic no-input Bravo foreground')
    Start-Sleep -Milliseconds 2600
    if ([IdleNative]::Tick() -ne $inputTick -or [WindowProbe]::ForegroundPid() -ne $bravo.Id) {
        throw 'No-input native foreground switch not demonstrated; no input sent'
    }
    if (@(Rows | Where-Object { $_.process -eq 'screenytimelinebravo' }).Count) {
        throw 'Bravo was stored before native input resumed'
    }
    $inputAt = [DateTimeOffset]::Now
    if (![WindowProbe]::RefreshSyntheticInput($bravo.Id)) { throw 'Bravo synthetic input not sent' }
    [void](Wait-For { [IdleNative]::Tick() -ne $inputTick } 'new native input stamp')
    Start-Sleep -Milliseconds 2200
    if ([WindowProbe]::ForegroundPid() -ne $bravo.Id) { throw 'Bravo not foreground at pause' }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $history = Wait-For {
        $matched = @(Rows | Where-Object { $_.process -eq 'screenytimelinebravo' })
        if ($matched.Count) { ,@(Rows) }
    } 'idle and Bravo durable history after pause'

    $b = @($history | Where-Object { $_.process -eq 'screenytimelinebravo' })
    $idle = @($history | Where-Object { $_.process -eq 'idle / away' })
    if ($b.Count -ne 1 -or $idle.Count -lt 1 -or $b[0].duration -lt 1000) {
        throw 'Expected one positive Bravo visit and native idle rows'
    }
    $bravoStart = [DateTimeOffset]::Parse($b[0].start, [Globalization.CultureInfo]::InvariantCulture)
    $idleEnd = [DateTimeOffset]::Parse($idle[-1].end, [Globalization.CultureInfo]::InvariantCulture)
    if ($bravoStart -lt $inputAt -or $bravoStart -lt $idleEnd) {
        throw 'Bravo started before real input or overlapped native idle'
    }
    if (@(Rows | Where-Object { $_.process -eq 'screenytimelinebravo' }).Count -ne 1) {
        throw 'Repeated query returned a duplicate Bravo interval'
    }
    Write-Host "NATIVE PASS: programmatic foreground switched to Bravo with unchanged last-input stamp; after real input, one Bravo interval ($($b[0].duration)ms) began after idle ended; overlap=0ms."
} finally {
    foreach ($p in @($bravo, $alpha, $app)) { if ($p) { Stop-ScratchProcess $p } }
}
