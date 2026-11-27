param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$helpers = Join-Path $PSScriptRoot '../arn82'
Add-Type -Path "$helpers/Probe.cs"
Add-Type -Path "$helpers/Native.cs"
. "$helpers/window.ps1"

$local = [Environment]::GetFolderPath('LocalApplicationData')
$mode = if ($env:ARN87_AUMID) { 'msix' } else { 'loose' }
$defaultTarget = if ($mode -eq 'msix') { $env:ARN87_AUMID } else { $Exe }
$profile = if ($mode -eq 'msix') { $env:ARN87_PROFILE } else { $local }
$data = Join-Path $profile ScreenTimeTracker
$marker = Join-Path $profile Screeny
$db = Join-Path $data screentime.db
$lease = "$db.owner"
$sql = Join-Path $env:RUNNER_TEMP arn87-sqlite/ScreenyWorkflowProbe.dll
$scratch = Join-Path $env:RUNNER_TEMP arn87-probe
$ae = [Windows.Automation.AutomationElement]
$scope = [Windows.Automation.TreeScope]::Descendants
$mySession = (Get-Process -Id $PID).SessionId
if ((Get-Process Screeny,ScreenyAuditLauncher -ErrorAction SilentlyContinue) -or
    (Test-Path $data) -or (Test-Path $marker) -or
    (Test-Path (Join-Path $local ScreenTimeTracker)) -or (Test-Path (Join-Path $local Screeny))) {
    throw 'Not a fresh disposable profile; no app data opened.'
}
[void](New-Item -ItemType Directory -Path $scratch)
$fixtures = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$launchNumber = 0
$madeLooseProfile = $false

function Check($condition, [string]$message) { if (!$condition) { throw $message } }
function Note([string]$message) { "PASS $mode $message" | Write-Host }
function Wait-For([scriptblock]$condition, [string]$what, [int]$ms = 8500) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 45
    } while ($timer.ElapsedMilliseconds -lt $ms)
    throw "Did not observe $what"
}
function Input-Fixture([string]$name) {
    $fixture = Start-Process (Join-Path $env:RUNNER_TEMP "ScreenyAudit$name.exe") -PassThru
    $fixtures.Add($fixture)
    [void](Wait-For { [WindowProbe]::ForegroundPid() -eq $fixture.Id } "$name foreground")
    Check ([WindowProbe]::RefreshSyntheticInput($fixture.Id)) "No input refreshed for $name"
    $fixture
}
function Launch([string]$arg = '', [int]$count = 1, [string]$target = $defaultTarget, [string]$route = $mode) {
    $script:launchNumber++
    $request = Join-Path $scratch "launch-$launchNumber"
    [IO.File]::WriteAllLines($request, [string[]]@($route, $target, $arg, "$count"))
    $driver = Start-Process (Join-Path $env:RUNNER_TEMP ScreenyAuditLauncher.exe) -ArgumentList ('"{0}"' -f $request) -PassThru
    $fixtures.Add($driver)
    [void](Wait-For { Test-Path "$request.out" } 'foreground launcher result')
    $result = [IO.File]::ReadAllText("$request.out")
    Check ($result -match '^\d+(,\d+)*$') "Launch failed: $result"
    @{ Driver = $driver; Ids = @($result.Split(',') | ForEach-Object { [int]$_ }) }
}
function Owners {
    Get-Process -Name Screeny -ErrorAction SilentlyContinue | Where-Object SessionId -eq $mySession
}
function One-Owner([int]$expected = 0) {
    $only = Wait-For {
        $running = @(Owners)
        if ($running.Count -eq 1 -and (!$expected -or $running[0].Id -eq $expected)) { $running[0] }
    } "one Screeny PID (expected $expected)"
    $id = [WindowProbe]::Identity($only.Handle)
    Check ($(if ($mode -eq 'loose') { $id -eq 'unpackaged' } else { $id -like 'Screeny.Verification.ARN94_*' })) "Wrong owner identity: $id"
    $only
}
function Window-For($owner) {
    Wait-For {
        $handle = [WindowProbe]::WindowFor($owner.Id)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    } "WinUI HWND of $($owner.Id)"
}
function Foreground($owner, $window) {
    [void](Wait-For { [WindowProbe]::ForegroundPid() -eq $owner.Id -and
        [WindowProbe]::GetForegroundWindow() -eq $window -and [WindowProbe]::IsWindowVisible($window) } "existing Screeny HWND $window foreground ($($owner.Id)); observed foreground PID $([WindowProbe]::ForegroundPid())")
    [void](One-Owner $owner.Id)
}
function View($window, $owner) {
    $item = $ae::FromHandle($window)
    Check ($item -and $item.Current.ProcessId -eq $owner.Id) 'UIA root not owned by fixture Screeny'
    $item
}
function Find($root, $property, [string]$value) {
    $root.FindFirst($scope, [Windows.Automation.PropertyCondition]::new($property, $value))
}
function Status($owner, $window, [string]$want) {
    $root = View $window $owner
    [void](Wait-For {
        $status = Find $root ($ae::AutomationIdProperty) TrackingStatusText
        $status -and !$status.Current.IsOffscreen -and $status.Current.Name -eq $want
    } "tracking state $want")
}
function Pause($owner, $window) {
    $root = View $window $owner
    $button = Find $root ($ae::AutomationIdProperty) TrackingStatusButton
    Check ($button -and !$button.Current.IsOffscreen -and $button.Current.IsEnabled) 'Pause control not actionable'
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Status $owner $window Paused
}
function Hide($owner, $window) {
    Foreground $owner $window
    [AuditNative]::Keys(115,18)
    [void](Wait-For { ![WindowProbe]::IsWindowVisible($window) -and !$owner.HasExited } 'Alt+F4 hides but does not exit')
}
function Keep-Hidden($owner, $window, $driver) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Check (![WindowProbe]::IsWindowVisible($window)) 'Startup unexpectedly made owner visible'
        Check (!$owner.HasExited -and [WindowProbe]::ForegroundPid() -eq $driver.Id) 'Startup changed the owner or foreground'
        Start-Sleep -Milliseconds 45
    } while ($timer.ElapsedMilliseconds -lt 1400)
    [void](One-Owner $owner.Id)
}
function Sql([string]$sqlMode) {
    $value = & dotnet $sql $db $sqlMode
    if ($LASTEXITCODE -ne 0) { throw "SQLite snapshot failed: $sqlMode" }
    $value
}
function Rows([string]$name) { Sql history | Where-Object { $_.Split('/')[1] -eq "screenyaudit$name" } }
function Visit([string]$name) {
    $fixture = Input-Fixture $name
    Start-Sleep -Milliseconds 1650
    Check ([WindowProbe]::ForegroundPid() -eq $fixture.Id) "Unexpected focus loss during visit $name"
    [void](Input-Fixture Bravo)
}
function Exactly-One-Visit([string]$name) {
    [void](Wait-For { @(Rows $name).Count -ge 1 } "committed row for $name")
    $rows = @(Rows $name)
    Check ($rows.Count -eq 1) "Expected one committed $name row, saw $($rows.Count)"
    $parts = $rows[0].Split('/')
    Check (([long]$parts[4]) -ge 1200 -and ([long]$parts[4]) -lt 3000) "Implausible $name visit duration $($parts[4])"
    Note "foreground visit $name committed once, duration=$($parts[4])ms"
}
function Clean-Exit($owner, $window) {
    Foreground $owner $window
    $r = New-Object AuditNative+Rect
    Check (([AuditNative]::IconRect($window, [ref]$r) -eq 0)) 'Screeny icon not registered; tray route unavailable'
    Check ([WindowProbe]::PostMessage($window, 1025, [IntPtr]::Zero, [IntPtr]517)) 'Tray context callback not queued'
    $menu = Wait-For {
        $popup = [AuditNative]::FindWindow('#32768', $null)
        if ($popup -and [AuditNative]::OwnerPid($popup) -eq $owner.Id -and [WindowProbe]::IsWindowVisible($popup)) { $popup }
    } 'visible owner tray menu'
    $bounds = Wait-For {
        $item = New-Object AuditNative+Rect
        if ([AuditNative]::ExitMenuBounds($menu, [ref]$item)) { $item }
    } 'Exit command ID, text, and hit-tested bounds in the real native menu'
    [void][AuditNative]::SetCursorPos([int](($bounds.Left + $bounds.Right) / 2), [int](($bounds.Top + $bounds.Bottom) / 2))
    [AuditNative]::Mouse($false)
    Check ($owner.WaitForExit(9000)) 'Real tray Exit did not stop the owner'
    Check ($owner.ExitCode -eq 0 -and @(Owners).Count -eq 0) 'Owner did not exit normally'
    Note "native tray Exit action stopped PID=$($owner.Id) cleanly (owned callback injected, native menu clicked)"
}

try {
    $first = Launch '--startup'
    $owner = One-Owner $first.Ids[0]
    $window = Window-For $owner
    Foreground $owner $window
    Status $owner $window Active
    Check ((Test-Path (Join-Path $marker '.firstrun')) -and (Test-Path $db) -and (Test-Path $lease)) 'Fresh startup did not create expected profile files'
    Check (@(Sql history).Count -eq 0) 'Synthetic history unexpectedly present on first launch'
    Note "fresh --startup visible with PID=$($owner.Id) HWND=$window and expected database profile"

    Hide $owner $window
    $startup = Launch '--startup' 3
    Keep-Hidden $owner $window $startup.Driver
    Note "three duplicate startup requests preserved hidden owner PID=$($owner.Id) HWND=$window"

    $normal = Launch
    Foreground $owner $window
    Status $owner $window Active
    Note "normal launch restored the original active PID=$($owner.Id) HWND=$window (request PID=$($normal.Ids -join ','))"
    Visit Alpha
    Exactly-One-Visit Alpha
    Check ((Sql duplicate) -eq '0/0') 'Alpha visit overlaps another committed Alpha row'
    $stable = @(Sql history) -join "`n"

    [void](Launch '' 4)
    Foreground $owner $window
    Status $owner $window Active
    Note 'four simultaneous normal requests preserved active intent and owner'
    if ($mode -eq 'loose') {
        $copy = Join-Path $scratch alternate
        Copy-Item -Path (Split-Path $Exe) -Destination $copy -Recurse
        Hide $owner $window
        [void](Launch '' 1 (Join-Path $copy Screeny.exe))
        Foreground $owner $window
        Status $owner $window Active
        Note 'alternate loose executable path restored the same profile owner'
    }

    Pause $owner $window
    Hide $owner $window
    $startup = Launch '--startup'
    Keep-Hidden $owner $window $startup.Driver
    [void](Launch 'note=--startup-example')
    Foreground $owner $window
    Status $owner $window Paused
    [void](Launch)
    Foreground $owner $window
    Status $owner $window Paused
    Visit PausedOne
    Check (@(Rows PausedOne).Count -eq 0 -and (@(Sql history) -join "`n") -eq $stable) 'Paused reopened instance wrote or changed history'
    Note 'repeated paused relaunch stayed paused with zero paused visit rows and byte-for-byte matching synthetic row snapshot'

    [void](Launch)
    Foreground $owner $window
    Clean-Exit $owner $window
    $old = $owner.Id

    $startup = Launch '--startup'
    $owner = One-Owner $startup.Ids[0]
    Check ($owner.Id -ne $old) 'Startup reopen did not acquire a new owner'
    $window = Window-For $owner
    Keep-Hidden $owner $window $startup.Driver
    Visit ActiveOne
    Exactly-One-Visit ActiveOne
    [void](Launch)
    Foreground $owner $window
    Status $owner $window Active
    Check ((@(Rows Alpha) -join "`n") -eq $stable) 'Clear exit/reopen modified original history'
    Note 'clean Exit released ownership; subsequent startup stayed hidden and tracked exactly once until normal reactivation'

    $old = $owner.Id
    $owner.Kill()
    Check ($owner.WaitForExit(6000) -and (Test-Path $lease)) 'Abandonment did not leave a stale marker behind'
    $snapshot = @(Sql history) -join "`n"
    $held = [IO.File]::Open($lease, 'Open', 'ReadWrite', 'None')
    try {
        $unrouted = Launch
        [void](Wait-For { @(Owners).Count -eq 0 } 'contender exited while profile held without an activation endpoint')
        Check ((@(Sql history) -join "`n") -eq $snapshot) 'Contended profile was touched without its activation route'
        Note 'lock held with no matching activation route rejected the contender without writes'
    } finally {
        $held.Dispose()
    }
    $burst = Launch '' 6
    $owner = One-Owner
    Check ($owner.Id -ne $old -and $burst.Ids -contains $owner.Id) 'Simultaneous reopen did not select one of the new invocations'
    $window = Window-For $owner
    Foreground $owner $window
    Status $owner $window Active
    Check ((@(Sql history) -join "`n") -eq $snapshot) 'Abandoned owner recovery changed committed history'
    Visit ActiveTwo
    Exactly-One-Visit ActiveTwo
    Check ((Sql overlap) -eq '0' -and @(Sql history).Count -eq 3) 'Unexpected synthetic coverage or overlapping history'
    Note "six cold simultaneous requests after abandoned lease selected one owner PID=$($owner.Id) HWND=$window, all three expected visits recorded once"

    if ($mode -eq 'msix') {
        Check (!(Test-Path (Join-Path $local ScreenTimeTracker)) -and !(Test-Path (Join-Path $local Screeny))) 'Packaged launch wrote into the loose profile'
        $madeLooseProfile = $true
        $other = Launch '' 1 $Exe loose
        $otherOwner = Get-Process -Id $other.Ids[0]
        $otherWindow = Window-For $otherOwner
        [void](Wait-For { [WindowProbe]::GetForegroundWindow() -eq $otherWindow } 'separate loose owner foreground')
        Check (!$owner.HasExited -and @(Owners).Count -eq 2 -and [WindowProbe]::Identity($otherOwner.Handle) -eq 'unpackaged') 'Packaged/loose profiles did not get distinct owners'
        Check ((Test-Path (Join-Path $local ScreenTimeTracker/screentime.db.owner)) -and (Test-Path $lease)) 'Loose and packaged lease files are not isolated'
        Check ((Sql overlap) -eq '0' -and @(Sql history).Count -eq 3) 'Loose launch changed package synthetic history'
        Note 'packaged and loose launches owned distinct profiles without cross-activation'
    }
} catch {
    "Failure diagnostics: expected mode=$mode; observed owner PIDs=$(@(Owners | ForEach-Object Id) -join ','); foreground=$([WindowProbe]::ForegroundPid()); db=$(Test-Path $db); lease=$(Test-Path $lease)" | Write-Host
    throw
} finally {
    Owners | ForEach-Object { Stop-ScratchProcess $_ }
    foreach ($fixture in $fixtures) { Stop-ScratchProcess $fixture }
    if ($mode -eq 'loose' -or $madeLooseProfile) {
        foreach ($path in (Join-Path $local Screeny),(Join-Path $local ScreenTimeTracker)) {
            if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
}
