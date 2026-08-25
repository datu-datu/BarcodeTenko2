[CmdletBinding()]
param(
    [ValidateSet("All", "Native", "Lite", "Viewer")]
    [string]$Target = "All",

    [string]$Configuration = "Release",

    [string]$OutputDir = "dist",

    [switch]$SkipTests,

    [switch]$SkipEncryption
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Resolve root directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
if (-not (Test-Path (Join-Path $RootDir "Tenko.sln"))) {
    if (Test-Path (Join-Path $ScriptDir "Tenko.sln")) {
        $RootDir = $ScriptDir
    }
}

$DistPath = Join-Path $RootDir $OutputDir
$DataDir = Join-Path $RootDir "data"
$ToolsDir = Join-Path $RootDir "tools"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " BarcodeTenko2 Publish Script" -ForegroundColor Cyan
Write-Host " Configuration : $Configuration" -ForegroundColor Gray
Write-Host " Target        : $Target" -ForegroundColor Gray
Write-Host " Root Directory: $RootDir" -ForegroundColor Gray
Write-Host " Output Dir    : $DistPath" -ForegroundColor Gray
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Update encrypted student data (for Native target)
if (-not $SkipEncryption -and ($Target -eq "All" -or $Target -eq "Native")) {
    $studentsCsv = Join-Path $DataDir "students.csv"
    $passphrase = Join-Path $DataDir "students.passphrase"
    $encryptScript = Join-Path $ToolsDir "Encrypt-StudentsCsv.ps1"

    if ((Test-Path $studentsCsv) -and (Test-Path $passphrase) -and (Test-Path $encryptScript)) {
        Write-Host "[1/4] Updating encrypted student data (students.enc)..." -ForegroundColor Yellow
        try {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $encryptScript -InputPath $studentsCsv -PassphrasePath $passphrase -OutputPath (Join-Path $DataDir "students.enc")
            Write-Host "  -> Encrypted: data/students.enc" -ForegroundColor Green
        }
        catch {
            Write-Warning "Failed to encrypt student data: $_"
        }
    }
    else {
        Write-Host "[1/4] students.csv or passphrase not found. Skipped encryption." -ForegroundColor Gray
    }
    Write-Host ""
}

# 2. Run automated tests
if (-not $SkipTests) {
    Write-Host "[2/4] Running automated tests (dotnet test)..." -ForegroundColor Yellow
    Push-Location $RootDir
    try {
        & dotnet test -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Tests failed. Publish aborted."
        }
        Write-Host "  -> All tests passed successfully." -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
    Write-Host ""
}
else {
    Write-Host "[2/4] Skipped tests (-SkipTests)" -ForegroundColor Gray
    Write-Host ""
}

# 3. Publish projects
$projectsToPublish = @()

if ($Target -eq "All" -or $Target -eq "Native") {
    $projectsToPublish += @{
        Name = "Tenko.Native"
        ProjectPath = Join-Path $RootDir "src\Tenko.Native\Tenko.Native.csproj"
        OutDir = Join-Path $DistPath "Tenko.Native"
    }
}

if ($Target -eq "All" -or $Target -eq "Lite") {
    $projectsToPublish += @{
        Name = "Tenko.Lite"
        ProjectPath = Join-Path $RootDir "src\Tenko.Lite\Tenko.Lite.csproj"
        OutDir = Join-Path $DistPath "Tenko.Lite"
    }
}

if ($Target -eq "All" -or $Target -eq "Viewer") {
    $projectsToPublish += @{
        Name = "ScanViewer"
        ProjectPath = Join-Path $RootDir "src\ScanViewer\ScanViewer.csproj"
        OutDir = Join-Path $DistPath "ScanViewer"
    }
}

Write-Host "[3/4] Publishing applications (dotnet publish)..." -ForegroundColor Yellow

foreach ($proj in $projectsToPublish) {
    $pName = $proj["Name"]
    $pPath = $proj["ProjectPath"]
    $pOut = $proj["OutDir"]

    Write-Host "  >> Publishing $pName -> $pOut" -ForegroundColor Cyan
    
    if (-not (Test-Path $pOut)) {
        New-Item -ItemType Directory -Path $pOut -Force | Out-Null
    }

    Push-Location $RootDir
    try {
        & dotnet publish $pPath -c $Configuration -o $pOut --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed for $pName."
        }
        Write-Host "     -> Succeeded: $pName" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
Write-Host ""

# 4. Security check
Write-Host "[4/4] Security check on published package..." -ForegroundColor Yellow
$forbiddenFiles = @("students.csv", "students.passphrase", "server.json")
$foundForbidden = @()

foreach ($file in (Get-ChildItem -Path $DistPath -Recurse -File)) {
    if ($forbiddenFiles -contains $file.Name) {
        $foundForbidden += $file.FullName
    }
}

if ($foundForbidden.Count -gt 0) {
    Write-Warning "WARNING: Sensitive files detected in distribution folder! Delete before distribution:"
    foreach ($f in $foundForbidden) {
        Write-Warning "  - $f"
    }
}
else {
    Write-Host "  -> Security check passed (no plaintext secrets in dist)." -ForegroundColor Green
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host " Publish completed successfully!" -ForegroundColor Green
Write-Host " Output: $DistPath" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host ""

# List published artifacts
foreach ($proj in $projectsToPublish) {
    $pName = $proj["Name"]
    $pOut = $proj["OutDir"]
    if (Test-Path $pOut) {
        Write-Host "[$pName]" -ForegroundColor Cyan
        Get-ChildItem -Path $pOut -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($pOut.Length + 1)
            $sizeMb = [math]::Round($_.Length / 1MB, 2)
            Write-Host "  - $rel ($sizeMb MB)" -ForegroundColor Gray
        }
        Write-Host ""
    }
}
