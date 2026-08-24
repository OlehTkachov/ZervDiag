param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

Write-Host "=== ZervDiag Beta build ==="
Write-Host "Repository: $repo"

& $Python -m pip install -r requirements.txt
& $Python -m pip install -r requirements-build.txt

Write-Host "`n[smoke] Database transfer and settings UI..."
& $Python ".\packaging\smoke_beta.py"
if ($LASTEXITCODE -ne 0) {
    throw "Beta database/settings smoke test failed"
}

foreach ($path in @("build", "build-scheduled", "dist")) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

Write-Host "`n[1/3] Building ZervDiag.exe..."
& $Python -m PyInstaller `
    --noconfirm `
    --clean `
    --windowed `
    --onedir `
    --name ZervDiag `
    --distpath dist `
    --workpath build `
    main.py

if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller failed while building ZervDiag.exe"
}

Write-Host "`n[2/3] Building ZervDiagScheduledIndex.exe..."
& $Python -m PyInstaller `
    --noconfirm `
    --clean `
    --noconsole `
    --onefile `
    --name ZervDiagScheduledIndex `
    --distpath dist `
    --workpath build-scheduled `
    run_scheduled_index.py

if ($LASTEXITCODE -ne 0) {
    throw "PyInstaller failed while building ZervDiagScheduledIndex.exe"
}

Copy-Item `
    ".\dist\ZervDiagScheduledIndex.exe" `
    ".\dist\ZervDiag\ZervDiagScheduledIndex.exe" `
    -Force

$programFilesX86 = ${env:ProgramFiles(x86)}
$innoCandidates = @()

if ($programFilesX86) {
    $candidate = Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) {
        $innoCandidates += $candidate
    }
}

if ($env:ProgramFiles) {
    $candidate = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) {
        $innoCandidates += $candidate
    }
}

if ($innoCandidates.Count -eq 0) {
    Write-Host "`n[3/3] Inno Setup not found."
    Write-Host "Application files are ready here:"
    Write-Host "  $repo\dist\ZervDiag"
    Write-Host "Install Inno Setup 6, then run this script again to create Setup.exe."
    exit 0
}

$inno = $innoCandidates[0]
Write-Host "`n[3/3] Building installer with Inno Setup..."
& $inno ".\packaging\ZervDiag_Beta.iss"

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed"
}

Write-Host "`nDONE"
Write-Host "Installer: $repo\dist\installer\ZervDiag_Beta_Setup.exe"
