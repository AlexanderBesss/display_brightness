# Brightness - Production Build Script
# Requires: .NET 8 SDK

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = Join-Path $ProjectDir "publish"

try {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Brightness - Build Script" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    # Check .NET SDK
    Write-Host "[1/4] Checking .NET SDK..." -ForegroundColor Yellow
    $dotnetVersion = dotnet --version
    if (-not $dotnetVersion) {
        Write-Host "ERROR: .NET SDK not found. Install .NET 8 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Red
        throw ".NET SDK not found"
    }
    Write-Host "  .NET SDK version: $dotnetVersion" -ForegroundColor Green

    # Clean
    Write-Host "[2/4] Cleaning previous builds..." -ForegroundColor Yellow
    if (Test-Path $PublishDir) {
        Remove-Item -Recurse -Force $PublishDir
    }
    dotnet clean "$ProjectDir" -v quiet

    # Restore
    Write-Host "[3/4] Restoring packages..." -ForegroundColor Yellow
    dotnet restore "$ProjectDir"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Restore failed." -ForegroundColor Red
        throw "Restore failed"
    }

    # Publish
    Write-Host "[4/4] Publishing (Release, win-x64, self-contained)..." -ForegroundColor Yellow
    dotnet publish "$ProjectDir" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o "$PublishDir"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Publish failed." -ForegroundColor Red
        throw "Publish failed"
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Build successful!" -ForegroundColor Green
    Write-Host "  Output: $PublishDir" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green

    $exe = Get-ChildItem "$PublishDir\Brightness.exe"
    if ($exe) {
        $size = [math]::Round($exe.Length / 1MB, 2)
        Write-Host "  Size: ${size} MB" -ForegroundColor Green
    }
}
catch {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  BUILD FAILED: $_" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
}

Write-Host ""
Write-Host "Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
