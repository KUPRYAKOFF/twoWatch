param([string]$Executable = "artifacts/win-x64/CinemaTogether.exe")
$ErrorActionPreference = "Stop"
$app = Start-Process (Resolve-Path $Executable) -PassThru
try {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 300
        $app.Refresh()
        if ($app.HasExited) { throw "Desktop exited before opening a window (exit code $($app.ExitCode))" }
    } while ($app.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($app.MainWindowHandle -eq 0) { throw "Desktop did not open a window" }
    if ($app.MainWindowTitle -notlike "*Cinema Together*") { throw "Unexpected desktop window: $($app.MainWindowTitle)" }
    Write-Host "PASS: WPF application opened its main window."
    $app.CloseMainWindow() | Out-Null
    if (!$app.WaitForExit(10000)) { throw "Desktop did not close cleanly" }
} catch {
    $logs = Join-Path $env:LOCALAPPDATA "CinemaTogether/logs"
    if (Test-Path $logs) {
        Get-ChildItem $logs -Filter *.log | ForEach-Object { Get-Content $_.FullName -Tail 30 }
    }
    throw
} finally {
    if (!$app.HasExited) { $app.Kill() }
}
