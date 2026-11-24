param(
    [switch]$IncludeFFmpeg,
    [string]$OutputDirectory = "artifacts/win-x64"
)
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet publish src/CinemaTogether.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Copy-Item README.md, THIRD-PARTY-NOTICES.md $OutputDirectory
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $commit = git rev-parse HEAD
    @("Cinema Together $version", "Commit: $commit", "Runtime: win-x64", "Self-contained: true") |
        Set-Content (Join-Path $OutputDirectory "BUILD-INFO.txt") -Encoding utf8
    if ($IncludeFFmpeg) {
        $archive = Join-Path $projectRoot "artifacts/ffmpeg-9.1-essentials_build.zip"
        $expectedHash = "B9BBBB3ACECE000CBEEC994F856649354E52C773807841E78908374B24D658FD"
        if (!(Test-Path $archive)) {
            Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.1-essentials_build.zip" -OutFile $archive
        }
        if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw "FFmpeg archive checksum mismatch" }
        $unpacked = Join-Path $projectRoot "artifacts/ffmpeg-unpacked"
        Expand-Archive $archive -DestinationPath $unpacked -Force
        $ffmpegRoot = Join-Path $unpacked "ffmpeg-9.1-essentials_build"
        $tools = Join-Path $OutputDirectory "tools"
        New-Item -ItemType Directory $tools -Force | Out-Null
        Copy-Item (Join-Path $ffmpegRoot "bin/ffmpeg.exe"), (Join-Path $ffmpegRoot "bin/ffprobe.exe") $tools
        Copy-Item (Join-Path $ffmpegRoot "LICENSE") (Join-Path $tools "FFmpeg-LICENSE.txt")
        Copy-Item (Join-Path $ffmpegRoot "README.txt") (Join-Path $tools "FFmpeg-README.txt")
    }
    $zip = Join-Path (Split-Path $OutputDirectory -Parent) "CinemaTogether-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$OutputDirectory/*" -DestinationPath $zip -CompressionLevel Optimal
    Get-FileHash $zip -Algorithm SHA256 | Format-List
    Write-Host "Portable Windows build: $zip"
} finally { Pop-Location }
