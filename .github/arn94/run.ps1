param([ValidateSet('baseline','fixed','composed')][string]$Case,
      [ValidateSet('loose','msix')][string]$Mode,
      [string]$Exe)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$env:RUNNER_TEMP -or ![Environment]::Is64BitProcess -or ![Environment]::UserInteractive) {
    throw 'Requires an interactive disposable Windows x64 GitHub runner.'
}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
Add-Type -Path "$PSScriptRoot/WindowProbe.cs"
Add-Type -Path "$PSScriptRoot/Native.cs"

$marker = 'ARN94_SYNTHETIC_812f678dbca14b5fad357a994f68d6d0'
$local = [Environment]::GetFolderPath('LocalApplicationData')
$scratch = Join-Path $env:RUNNER_TEMP $marker
$looseData = Join-Path $local ScreenTimeTracker
$legacy = 'ARN94_existing_log_fixture'
$owned = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$profiles = New-Object 'System.Collections.Generic.List[string]'
$packageNames = @('Screeny.Verification.ARN94','Screeny.Verification.ARN94Fault')
if ((Get-Process Screeny -ErrorAction SilentlyContinue) -or (Test-Path $scratch) -or
    (Test-Path $looseData) -or (Test-Path (Join-Path $local Screeny)) -or
    (Get-AppxPackage | Where-Object { $_.Name -in ($packageNames + 'ArnoGevorkyan.ScreenyScreenTime') })) {
    throw 'Non-fresh profile; no application data opened.'
}
[void](New-Item -ItemType Directory -Path $scratch)
function Check($condition, [string]$what) { if (!$condition) { throw $what } }
function Wait-For([scriptblock]$condition, [string]$what, [int]$ms = 12000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 60
    } while ($timer.ElapsedMilliseconds -lt $ms)
    throw "Timed out waiting for $what"
}
function Register([string]$name, [string]$package) {
    Add-AppxPackage -Path $package -AllowUnsigned
    $result = Get-AppxPackage -Name $name
    Check ($result.PackageFamilyName -like "$($name)_*" -and $result.Architecture -eq 'X64') 'Wrong unsigned x64 test identity'
    $profiles.Add((Join-Path $local "Packages/$($result.PackageFamilyName)/LocalCache/Local"))
    $result
}
function Launch([string]$target, [string]$argument) {
    if ($Mode -eq 'loose') {
        $proc = Start-Process -FilePath $target -ArgumentList $argument -WorkingDirectory $scratch -PassThru
    } else {
        $proc = Get-Process -Id ([DiagnosticsProbe]::Activate($target, $argument))
    }
    $owned.Add($proc)
    $expectedIdentity = if ($Mode -eq 'loose') { 'unpackaged' } else { 'Screeny.Verification.ARN94*' }
    Check ([WindowProbe]::Identity($proc.Handle) -like $expectedIdentity) 'Unexpected process package identity'
    $command = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)").CommandLine
    Check ($command -like "*$marker*") 'Synthetic marker did not reach the process command line'
    $proc
}
function Stop-Fixture($process) {
    if (!$process.HasExited) { $process.Kill(); Check ($process.WaitForExit(5000)) 'Fixture did not stop in five seconds' }
}
function Check-Window($process, [bool]$visible) {
    $hwnd = Wait-For { $handle = [WindowProbe]::WindowFor($process.Id); if ($handle -ne [IntPtr]::Zero) { $handle } } 'Screeny WinUI window'
    if (!$visible) { Start-Sleep -Milliseconds 600 }
    if ($visible) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
        [void](Wait-For {
            $status = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'TrackingStatusText'))
            $status -and !$status.Current.IsOffscreen -and $status.Current.Name -eq 'Active'
        } 'visible, active tracking UI')
    }
    Check ([WindowProbe]::IsWindowVisible($hwnd) -eq $visible) 'Unexpected Screeny window visibility'
}
function Logs {
    foreach ($profile in @($local) + $profiles.ToArray()) {
        $candidate = Join-Path $profile ScreenTimeTracker/app_log.txt
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
    }
}
function Log-Text {
    $all = @(Logs)
    Check ($all.Count -gt 0) 'No real app-data log found'
    ($all | ForEach-Object { [IO.File]::ReadAllText($_) }) -join "`n"
}
function Check-Safe([string]$text) {
    Check (!$text.Contains($marker)) 'Synthetic marker persisted'
    Check ($text -notmatch '(?m)Machine Name:|Current Directory:|App Directory:|Command line arguments count:|Arg\[\d+\]:|Stack Trace:|Detailed exception information:') 'An old diagnostic payload field persisted'
    Check (!$text.Contains([Environment]::MachineName) -and !$text.Contains($local)) 'Machine name or app-data path persisted'
}
function Check-Fault([string]$target, [bool]$logged) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $app = Launch $target $marker
    try {
        $dialog = Wait-For {
            $handle = [DiagnosticsProbe]::Dialog($app.Id)
            if ($handle -ne [IntPtr]::Zero) { $handle }
        } 'one native error dialog'
    } catch {
        $app.Refresh()
        $safe = Log-Text
        $exit = if ($app.HasExited) { '0x{0:X8}' -f $app.ExitCode } else { 'still-running' }
        "Fault trace: exit=$exit app-launch-error=$($safe -match 'Application launch failed:') app-init-error=$($safe -match 'App initialization failed:') ui-error=$($safe -match 'Unhandled UI exception:') window-created=$(@([regex]::Matches($safe, 'MainWindow created successfully')).Count)"
        throw
    }
    $text = [DiagnosticsProbe]::Text($dialog)
    Check-Safe $text
    Check ($text -match 'The application failed to start properly\.' -and $text -match 'Error code: (0x[0-9A-F]{8})') 'Fatal dialog lost its English headline or HRESULT'
    $code = $Matches[1]
    $status = if ($logged) { 'See app_log.txt in local app data for details.' } else { 'No diagnostic entry was saved.' }
    Check ($text.Contains($status)) 'Fatal dialog misstated file logging result'
    $root = [Windows.Automation.AutomationElement]::FromHandle($dialog)
    $ok = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'OK'))
    if ($ok) {
        $ok.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    } else {
        Check ([DiagnosticsProbe]::Dismiss($dialog)) 'Unable to dismiss the fatal dialog'
    }
    Check ($app.WaitForExit(5000) -and $app.ExitCode -eq 1) 'Fatal dialog did not exit once with code 1'
    if ($logged) {
        $log = Log-Text
        Check-Safe $log
        Check (@([regex]::Matches($log, "Application launch failed: TypeInitializationException \($code\); caused by InvalidOperationException \(0x80131509\)")).Count -eq 1) 'Expected one safe fatal log entry matching the dialog HRESULT'
    }
    "PASS $Case $Mode fault logged=$logged exit=1 dialog-to-exit-total-ms=$($clock.ElapsedMilliseconds)"
}
try {
    if ($Mode -eq 'loose') {
        $target = $Exe
    } else {
        $normal = Register $packageNames[0] $env:ARN94_NORMAL_PACKAGE
        $target = "$($normal.PackageFamilyName)!App"
    }
    $app = Launch $target "--startup $marker"
    Check-Window $app $true
    Stop-Fixture $app
    $app = Launch $target $marker
    Check-Window $app $true
    Stop-Fixture $app
    $app = Launch $target "--startup $marker"
    Check-Window $app $false
    Stop-Fixture $app
    $text = Log-Text
    Check ($text -match 'MainWindow activated \(first run\)' -and $text -match 'MainWindow activated \(normal launch\)' -and
        $text -match 'MainWindow created but not activated \(startup launch - running in background\)') 'Missing launch classifications'
    if ($Case -eq 'baseline') {
        Check ($text.Contains($marker) -and $text -match '(?m)Machine Name:|Current Directory:|App Directory:') 'Baseline did not capture the fixture and old fields'
        "PASS $Case $Mode positive-control leak and matching native launch states"
        return
    }
    Check-Safe $text
    "PASS $Case $Mode first-run startup visible, manual visible and Active, established startup hidden; marker and old fields absent"
    if ($Mode -eq 'msix') {
        $fault = Register $packageNames[1] $env:ARN94_FAULT_PACKAGE
        $faultTarget = "$($fault.PackageFamilyName)!App"
        $faultDir = $fault.InstallLocation
        $faultLog = Join-Path $profiles[$profiles.Count - 1] ScreenTimeTracker/app_log.txt
    } else {
        $faultDir = Join-Path $scratch fault
        Copy-Item -LiteralPath (Split-Path -Parent $Exe) -Destination $faultDir -Recurse
        $faultTarget = Join-Path $faultDir Screeny.exe
        $chart = Join-Path $faultDir LiveChartsCore.dll
        Check (Test-Path -LiteralPath $chart -PathType Leaf) 'Missing managed chart dependency'
        $productDll = Join-Path $faultDir Screeny.dll
        $hash = (Get-FileHash $productDll).Hash
        dotnet run --project "$PSScriptRoot/fault/Fault.csproj" -- $chart $marker
        Check ($LASTEXITCODE -eq 0 -and (Get-FileHash $productDll).Hash -eq $hash) 'Fault fixture changed the product binary or failed'
        $faultLog = Join-Path $looseData app_log.txt
        $legacyPath = Join-Path $faultDir Screeny_ErrorLog.txt
        [IO.File]::WriteAllText($legacyPath, $legacy)
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        & icacls.exe $faultDir /deny "*${sid}:(WD,AD,WEA,WA)" | Out-Null
        Check ($LASTEXITCODE -eq 0) 'Could not deny writes in disposable exe directory'
        Check ([IO.File]::Exists($faultTarget) -and (Get-FileHash $productDll).Hash -eq $hash) 'Fixture no longer readable'
    }
    $writeAttempt = Join-Path $faultDir "$marker.txt"
    try {
        [IO.File]::WriteAllText($writeAttempt, $marker)
        throw 'Fault executable directory was writable'
    } catch [UnauthorizedAccessException] {}
    Check-Fault $faultTarget $true
    if ($Mode -eq 'msix' -and !(Test-Path -LiteralPath $faultLog -PathType Leaf)) {
        $faultLog = Join-Path $looseData app_log.txt
    }
    Check (Test-Path -LiteralPath $faultLog -PathType Leaf) 'Fault sink not found'
    $before = (Get-FileHash -LiteralPath $faultLog).Hash
    $lock = [IO.File]::Open($faultLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Check-Fault $faultTarget $false } finally { $lock.Dispose() }
    Check ((Get-FileHash -LiteralPath $faultLog).Hash -eq $before) 'Write-denied log changed'
    $old = [Text.Encoding]::UTF8.GetBytes($legacy)
    $cap = New-Object byte[] (1024 * 1024)
    [Array]::Copy($old, $cap, $old.Length)
    [IO.File]::WriteAllBytes($faultLog, $cap)
    $before = (Get-FileHash -LiteralPath $faultLog).Hash
    Check-Fault $faultTarget $false
    Check ((Get-FileHash -LiteralPath $faultLog).Hash -eq $before) 'Capped existing log was changed or truncated'
    if ($Mode -eq 'loose') {
        Check ([IO.File]::ReadAllText($legacyPath) -eq $legacy) 'Existing executable-side log changed'
        Move-Item -LiteralPath $looseData -Destination (Join-Path $scratch displaced-app-data)
        [IO.File]::WriteAllText($looseData, $legacy)
        if ($Case -eq 'fixed') {
            Check-Fault $faultTarget $false
        } else {
            $app = Start-Process -FilePath $faultTarget -ArgumentList $marker -WorkingDirectory $scratch -PassThru
            $owned.Add($app)
            Check ($app.WaitForExit(5000) -and $app.ExitCode -eq 1) '#33 pre-WinUI profile failure did not return its failure status'
        }
        Check ([IO.File]::ReadAllText($looseData) -eq $legacy) 'Blocked profile path was clobbered'
    } else {
        Check (!(Test-Path -LiteralPath (Join-Path $faultDir Screeny_ErrorLog.txt))) 'Executable-side error log was created'
    }
    "PASS $Case $Mode unwritable executable folder, unavailable and capped app-data log, no existing diagnostic writes or truncation"
} finally {
    $owned | ForEach-Object { Stop-Fixture $_ }
    Get-AppxPackage | Where-Object { $_.Name -in $packageNames } | Remove-AppxPackage
}
