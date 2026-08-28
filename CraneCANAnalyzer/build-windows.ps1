$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $projectRoot "src\CraneCAN.App\CraneCAN.App.csproj"
$smokeProject = Join-Path $projectRoot "tests\CraneCAN.SmokeTests\CraneCAN.SmokeTests.csproj"
$publishDirectory = Join-Path $projectRoot "publish\onk160-test-win-x64"

function Assert-DotNetSuccess([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$step failed. dotnet exit code: $LASTEXITCODE"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: .NET 8 SDK was not found." -ForegroundColor Red
    Write-Host "Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

$sdkVersion = (& dotnet --version)
Assert-DotNetSuccess "Checking .NET SDK"
Write-Host ".NET SDK found: $sdkVersion"

Write-Host "Restoring and checking ONK-160 core..."
& dotnet restore $smokeProject
Assert-DotNetSuccess "Smoke-test restore"
& dotnet build $smokeProject -c Release --no-restore
Assert-DotNetSuccess "Smoke-test build"
& dotnet run --project $smokeProject -c Release --no-build
Assert-DotNetSuccess "Smoke tests"

if (Test-Path -LiteralPath $publishDirectory) {
    Write-Host "Removing the previous ONK-160 test build..."
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

Write-Host "Restoring self-contained Windows x64 assets..."
& dotnet restore $appProject -r win-x64 -p:SelfContained=true
Assert-DotNetSuccess "Windows x64 restore"

Write-Host "Publishing CraneCAN ONK-160 Test 0.4.1..."
& dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory
Assert-DotNetSuccess "Windows x64 publish"

$publishedExe = Join-Path $publishDirectory "CraneCAN.ONK160.Test.exe"
$publishedReadme = Join-Path $publishDirectory "README.md"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published executable was not created: $publishedExe"
}
if (-not (Test-Path -LiteralPath $publishedReadme)) {
    throw "Operator README was not copied to the publish directory: $publishedReadme"
}

$hash = (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash
$hashLine = "$hash *CraneCAN.ONK160.Test.exe"
Set-Content -LiteralPath (Join-Path $publishDirectory "SHA256SUMS.txt") -Value $hashLine -Encoding ascii

Write-Host ""
Write-Host "READY: $publishDirectory" -ForegroundColor Green
Write-Host "Run: CraneCAN.ONK160.Test.exe"
Write-Host "Read first: README.md"
Write-Host "The target PC does not need .NET. Install only the USB-UART VCP driver."
Write-Host "ONK-160: COM port, 38400 bit/s, 8E1, receive only."
