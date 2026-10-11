# Brightness - Production Build Script
# Requires: .NET 8 SDK

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = Join-Path $ProjectDir "publish"
$BuildSucceeded = $false

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

    # A running instance built from this folder holds its image open, which makes the
    # delete below fail. Stop only those instances; an installed copy elsewhere is not
    # in the way and is left alone.
    $lockingProcesses = @(Get-CimInstance Win32_Process -Filter "Name='Brightness.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($PublishDir, [System.StringComparison]::OrdinalIgnoreCase) })
    foreach ($process in $lockingProcesses) {
        Write-Host "  Stopping running instance (PID $($process.ProcessId)) that locks the build output." -ForegroundColor Yellow
        # A forced stop is the only option: the app cancels window close to stay in the
        # tray, so there is no graceful shutdown path to ask for. The shell drops the
        # icon on its own once the window handle that registered it dies.
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($lockingProcesses.Count -gt 0) {
        Start-Sleep -Milliseconds 500
    }

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
    Write-Host "[4/4] Publishing (Release, win-x64, small single file)..." -ForegroundColor Yellow
    dotnet publish "$ProjectDir" `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=false `
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
    Write-Host "  Requires: .NET 8 Desktop Runtime x64" -ForegroundColor Yellow

    $exe = Get-ChildItem "$PublishDir\Brightness.exe"
    if ($exe) {
        $size = [math]::Round($exe.Length / 1MB, 2)
        Write-Host "  Size: ${size} MB" -ForegroundColor Green
    }

    $BuildSucceeded = $true
}
catch {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  BUILD FAILED: $_" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
}

if (-not [Console]::IsInputRedirected) {
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}

exit $(if ($BuildSucceeded) { 0 } else { 1 })
