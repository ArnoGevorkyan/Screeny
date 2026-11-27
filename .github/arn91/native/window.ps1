function Get-ScreenyWindow($process) {
    $limit = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw ('Screeny exited: {0} (0x{0:X8})' -f $process.ExitCode) }
        $handle = $process.MainWindowHandle
        if ($handle -eq [IntPtr]::Zero) { continue }
        $className = [Text.StringBuilder]::new(256)
        [void][WindowProbe]::GetClassName($handle, $className, $className.Capacity)
        $reply = [UIntPtr]::Zero
        if ($className.ToString() -match 'WinUI' -and [WindowProbe]::IsWindowVisible($handle) -and
            [WindowProbe]::SendMessageTimeout($handle, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 2, 700, [ref]$reply) -ne [IntPtr]::Zero) {
            "Responsive window: class=$className; pid=$($process.Id); session=$($process.SessionId); identity=$([WindowProbe]::Identity($process.Handle))" | Write-Host
            return $handle
        }
    } while ($limit.Elapsed.TotalSeconds -lt 15)
    throw 'Screeny did not expose a visible responding WinUI window'
}

function Invoke-ScreenyUia($handle, $appProcessId, $mode) {
    $inputScript = Join-Path $PSScriptRoot status.ps1
    $stdout = Join-Path $env:RUNNER_TEMP "arn59-$mode-uia.out"
    $stderr = Join-Path $env:RUNNER_TEMP "arn59-$mode-uia.err"
    $uia = Start-Process "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-Mta', '-File', ('"{0}"' -f $inputScript),
        '-Window', $handle.ToInt64(), '-ScreenyPid', $appProcessId, '-Mode', $mode
    ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        if (!$uia.WaitForExit(13000)) {
            $uia.Kill()
            Write-Warning "UI Automation $mode timed out"
            return $false
        }
        Get-Content $stdout -Encoding utf8 | Write-Host
        Get-Content $stderr -Encoding utf8 | Write-Host
        "UI Automation $mode exit=$($uia.ExitCode)" | Write-Host
        return ($uia.ExitCode -eq 0)
    } finally {
        $uia.Dispose()
    }
}

function Stop-ScratchProcess($process) {
    if ($process) {
        if (!$process.HasExited) {
            $process.Kill()
            [void]$process.WaitForExit(4000)
        }
        $process.Dispose()
    }
}
