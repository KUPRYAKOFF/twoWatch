param([string]$Executable = "artifacts/win-x64/CinemaTogether.exe")
$ErrorActionPreference = "Stop"
$executablePath = (Resolve-Path $Executable).Path
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("cinema-smoke-" + [Guid]::NewGuid().ToString("N"))
$data = Join-Path $fixture "data"
New-Item -ItemType Directory $data -Force | Out-Null
$settingsPath = Join-Path $data "settings.json"
@{
    FfmpegPath = (Join-Path $fixture "old-install/tools/ffmpeg.exe")
    FfprobePath = (Join-Path $fixture "old-install/tools/ffprobe.exe")
} | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
$start = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
$start.UseShellExecute = $false
$start.WorkingDirectory = $fixture
$start.EnvironmentVariables["CINEMA_DATA_DIR"] = $data
$app = [System.Diagnostics.Process]::Start($start)
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
    if (Test-Path (Join-Path (Split-Path $executablePath) "tools/ffprobe.exe")) {
        $repairDeadline = (Get-Date).AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 200
            $stream = [System.IO.File]::Open($settingsPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
                ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
            $reader = [System.IO.StreamReader]::new($stream)
            try { $saved = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        } while (($saved.FfprobePath -ne "tools/ffprobe.exe" -or $saved.FfmpegPath -ne "tools/ffmpeg.exe") -and (Get-Date) -lt $repairDeadline)
        if ($saved.FfprobePath -ne "tools/ffprobe.exe" -or $saved.FfmpegPath -ne "tools/ffmpeg.exe") {
            throw "Desktop did not repair saved paths from the old installation"
        }
        Write-Host "PASS: stale FFmpeg/FFprobe paths were repaired; app started from a different working directory."
    }
    $app.CloseMainWindow() | Out-Null
    if (!$app.WaitForExit(10000)) { throw "Desktop did not close cleanly" }
} catch {
    $logs = Join-Path $data "logs"
    if (Test-Path $logs) {
        Get-ChildItem $logs -Filter *.log | ForEach-Object { Get-Content $_.FullName -Tail 30 }
    }
    throw
} finally {
    if (!$app.HasExited) { $app.Kill() }
    $app.WaitForExit()
    Remove-Item $fixture -Recurse -Force
}
