$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $projectRoot "src\CraneCAN.App\CraneCAN.App.csproj"
$smokeProject = Join-Path $projectRoot "tests\CraneCAN.SmokeTests\CraneCAN.SmokeTests.csproj"
$publishDirectory = Join-Path $projectRoot "publish\soosan-field-win-x64"
$publishedExe = Join-Path $publishDirectory "CraneCAN.Field.exe"
$publishedReadme = Join-Path $publishDirectory "README.md"
$publishedFieldGuide = Join-Path $publishDirectory "docs\SOOSAN_FIELD_CAPTURE.md"

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
if (-not $sdkVersion.StartsWith("8.")) {
    throw "The build requires .NET 8 SDK. Found: $sdkVersion"
}
Write-Host ".NET SDK found: $sdkVersion"

Write-Host "Restoring, building and running all smoke tests (ONK + Generic CAN)..."
& dotnet restore $smokeProject
Assert-DotNetSuccess "Smoke-test restore"
& dotnet build $smokeProject -c Release --no-restore
Assert-DotNetSuccess "Smoke-test build"
& dotnet run --project $smokeProject -c Release --no-build
Assert-DotNetSuccess "Smoke tests"

if (Test-Path -LiteralPath $publishDirectory) {
    Write-Host "Removing the previous SOOSAN field build..."
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

Write-Host "Restoring self-contained Windows x64 assets..."
& dotnet restore $appProject -r win-x64 -p:SelfContained=true
Assert-DotNetSuccess "Windows x64 restore"

Write-Host "Publishing CraneCAN Field 0.5.0 for SOOSAN JK1200A..."
& dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory
Assert-DotNetSuccess "Windows x64 publish"

if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published executable was not created: $publishedExe"
}
if (-not (Test-Path -LiteralPath $publishedReadme)) {
    throw "Field README was not copied to the publish directory: $publishedReadme"
}
if (-not (Test-Path -LiteralPath $publishedFieldGuide)) {
    throw "SOOSAN field guide was not copied to the publish directory: $publishedFieldGuide"
}

$hash = (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash.ToLowerInvariant()
$hashLine = "$hash *CraneCAN.Field.exe"
Set-Content -LiteralPath (Join-Path $publishDirectory "SHA256SUMS.txt") -Value $hashLine -Encoding ascii

Write-Host ""
Write-Host "READY: $publishDirectory" -ForegroundColor Green
Write-Host "Run: CraneCAN.Field.exe"
Write-Host "Read first: README.md and docs\SOOSAN_FIELD_CAPTURE.md"
Write-Host "The target PC does not need .NET Runtime. Copy the entire publish folder."
Write-Host "CraneCAN Field is offline-only: PCAN-View records TRC in Listen-only mode."
