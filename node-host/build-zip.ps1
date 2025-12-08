param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Write-Info($msg) {
    Write-Host "[build-zip] $msg"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "OverlayHud\OverlayHud.csproj"
$publishDir = Join-Path $repoRoot "OverlayHud\bin\$Configuration\net8.0-windows\win-x64\publish"
$publicDir = Join-Path $repoRoot "node-host\public"
$zipPath = Join-Path $publicDir "OverlayHud-win-x64.zip"
$webViewInstallerName = "MicrosoftEdgeWebView2RuntimeInstaller.exe"
$webViewInstallerPath = Join-Path $publishDir $webViewInstallerName
# Evergreen Standalone installer (machine-wide) to avoid requiring WebView2 on Win10
$webViewInstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124701"

Write-Info "Publishing WPF app (self-contained, multi-file)..."
dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    -p:PublishSingleFile=false `
    -p:SelfContained=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo

if (-not (Test-Path $webViewInstallerPath)) {
    Write-Info "Downloading WebView2 Evergreen bootstrapper..."
    try {
        Invoke-WebRequest -Uri $webViewInstallerUrl -OutFile $webViewInstallerPath -UseBasicParsing
    } catch {
        Write-Warning "Failed to download WebView2 installer: $($_.Exception.Message)"
    }
}

if (-not (Test-Path $publishDir)) {
    throw "Publish output not found at $publishDir"
}

if (-not (Test-Path $publicDir)) {
    New-Item -ItemType Directory -Path $publicDir | Out-Null
}

Write-Info "Creating optimized zip..."
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Info "Done. Zip ready at $zipPath"

