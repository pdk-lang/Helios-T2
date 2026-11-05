param(
    [string]$Version = "",
    [switch]$BumpPatch,
    [switch]$BumpMinor,
    [switch]$BumpMajor,
    [string]$ReleaseNotes = "Helios T2 - Thermal Fan Curve, Processor Speed Manager & 80% Battery Health Limiter with built-in plain-English setting guides. Built with AI assistance (zero coding skills).",
    [string]$GitHubToken = "",
    [string]$RepoName = "Helios-T2",
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$distDir = Join-Path $dir "dist"
if (!(Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }

# -----------------------------------------------------------------------------
# 1. Version Management
# -----------------------------------------------------------------------------
$verFile = Join-Path $dir "VERSION.txt"
$currentVer = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { "1.0.0" }

if ($Version -ne "") {
    $currentVer = $Version
} elseif ($BumpMajor -or $BumpMinor -or $BumpPatch) {
    $parts = $currentVer.Split('.')
    $maj = [int]$parts[0]
    $min = [int]$parts[1]
    $pat = [int]$parts[2]
    if ($BumpMajor) { $maj++; $min = 0; $pat = 0 }
    elseif ($BumpMinor) { $min++; $pat = 0 }
    elseif ($BumpPatch) { $pat++ }
    $currentVer = "$maj.$min.$pat"
}

[System.IO.File]::WriteAllText($verFile, $currentVer)
Write-Host "=================================================================" -ForegroundColor Yellow
Write-Host "  Helios T2 // Builder & GitHub Release Publisher                " -ForegroundColor Yellow
Write-Host "  Target Version: v$currentVer                                   " -ForegroundColor Yellow
Write-Host "=================================================================" -ForegroundColor Yellow

# Update AssemblyVersion inside Installer.cs
$instCsPath = Join-Path $dir "Installer.cs"
$instCs = [System.IO.File]::ReadAllText($instCsPath)
$instCs = [regex]::Replace($instCs, 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$currentVer.0`")")
$instCs = [regex]::Replace($instCs, 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$currentVer.0`")")
[System.IO.File]::WriteAllText($instCsPath, $instCs)

# -----------------------------------------------------------------------------
# 2. Compile MacAirControl.exe
# -----------------------------------------------------------------------------
$stagingExe = Join-Path $distDir "MacAirControl.exe"
Write-Host "[1/3] Compiling MacAirControl.cs -> dist\MacAirControl.exe..." -ForegroundColor Cyan
& $csc /nologo /target:winexe `
    /out:"$stagingExe" `
    /win32icon:"$dir\app.ico" `
    /win32manifest:"$dir\app.manifest" `
    /r:"$wpfDir\PresentationFramework.dll","$wpfDir\PresentationCore.dll","$wpfDir\WindowsBase.dll",System.Xaml.dll,System.Drawing.dll,System.Windows.Forms.dll,System.ServiceProcess.dll,System.dll `
    "$dir\MacAirControl.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Failed to compile MacAirControl.cs (Exit code $LASTEXITCODE)"
}

# -----------------------------------------------------------------------------
# 3. Compile Self-Contained Standalone Installer (.exe)
# -----------------------------------------------------------------------------
$setupVersionedExe = Join-Path $distDir ("HeliosT2_Setup_v" + $currentVer + ".exe")
$setupLatestExe    = Join-Path $distDir "HeliosT2_Setup_Latest.exe"

Write-Host "[2/3] Building Standalone Setup Installer -> $setupVersionedExe..." -ForegroundColor Cyan
& $csc /nologo /target:winexe `
    /out:"$setupVersionedExe" `
    /win32icon:"$dir\app.ico" `
    /win32manifest:"$dir\app.manifest" `
    /res:"$stagingExe,MacAirControl.exe" `
    /res:"$dir\applesmc.sys,applesmc.sys" `
    /res:"$dir\app.ico,app.ico" `
    /res:"$dir\README.md,README.md" `
    /res:"$verFile,VERSION.txt" `
    /r:"$wpfDir\PresentationFramework.dll","$wpfDir\PresentationCore.dll","$wpfDir\WindowsBase.dll",System.Xaml.dll,System.dll `
    "$instCsPath"

if ($LASTEXITCODE -ne 0) {
    throw "Failed to compile Installer.cs (Exit code $LASTEXITCODE)"
}

Copy-Item $setupVersionedExe $setupLatestExe -Force
$sizeKb = [Math]::Round((Get-Item $setupVersionedExe).Length / 1KB, 1)
Write-Host "      Built: $setupVersionedExe ($sizeKb KB)" -ForegroundColor Green

if ($BuildOnly) {
    Write-Host "[3/3] BuildOnly specified - skipping GitHub upload." -ForegroundColor Green
    exit 0
}

# -----------------------------------------------------------------------------
# 4. GitHub REST API Upload (Source Files + Tagged Release + .exe Asset)
# -----------------------------------------------------------------------------
$tokenFile = Join-Path $dir ".github_token"
if ([string]::IsNullOrWhiteSpace($GitHubToken) -and (Test-Path $tokenFile)) {
    $GitHubToken = (Get-Content $tokenFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
    throw "No GitHub token found. Pass -GitHubToken '<token>' or save it to $tokenFile"
}
[System.IO.File]::WriteAllText($tokenFile, $GitHubToken)

$headers = @{
    "Authorization" = "token $GitHubToken"
    "Accept"        = "application/vnd.github.v3+json"
    "User-Agent"    = "HeliosT2-Publisher"
}

Write-Host "[3/3] Connecting to GitHub API..." -ForegroundColor Cyan
$userResp = Invoke-RestMethod -Uri "https://api.github.com/user" -Headers $headers -Method Get
$owner = $userResp.login
Write-Host "      Authenticated as GitHub user: $owner" -ForegroundColor Green

$repoDesc = "Helios T2 - Thermal Fan Curve, Processor Speed Manager & 80% Battery Limiter for T2 Laptops running Windows 10/11 (Built with AI)"

# Check if old repo name exists and rename it to Helios-T2
$oldRepoUrl = "https://api.github.com/repos/$owner/MacBookAir-T2-ControlCenter"
try {
    $oldRepo = Invoke-RestMethod -Uri $oldRepoUrl -Headers $headers -Method Get
    if ($oldRepo.name -ne $RepoName) {
        Write-Host "      Renaming repository '$($oldRepo.name)' -> '$RepoName'..." -ForegroundColor Cyan
        $patchBody = @{
            name        = $RepoName
            description = $repoDesc
        } | ConvertTo-Json
        $null = Invoke-RestMethod -Uri $oldRepoUrl -Headers $headers -Method Patch -Body $patchBody -ContentType "application/json"
        Start-Sleep -Seconds 2
    }
} catch { }

# Ensure Repository Exists
$repoUrl = "https://api.github.com/repos/$owner/$RepoName"
$repoExists = $false
try {
    $null = Invoke-RestMethod -Uri $repoUrl -Headers $headers -Method Get
    $repoExists = $true
} catch {
    $repoExists = $false
}

if (-not $repoExists) {
    Write-Host "      Creating public GitHub repository '$owner/$RepoName'..." -ForegroundColor Cyan
    $createBody = @{
        name        = $RepoName
        description = $repoDesc
        private     = $false
        auto_init   = $true
    } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "https://api.github.com/user/repos" -Headers $headers -Method Post -Body $createBody -ContentType "application/json"
    Start-Sleep -Seconds 2
}

# Upload / Update Repository Files via Contents API
$filesToUpload = @(
    "README.md",
    "LICENSE",
    ".gitignore",
    "VERSION.txt",
    "MacAirControl.cs",
    "Installer.cs",
    "Publish-Release.ps1",
    "Install-SmcDriver.ps1",
    "app.manifest",
    "app.ico",
    "applesmc.sys"
)

foreach ($relPath in $filesToUpload) {
    $fullPath = Join-Path $dir $relPath
    if (!(Test-Path $fullPath)) { continue }

    $bytes = [System.IO.File]::ReadAllBytes($fullPath)
    $b64 = [Convert]::ToBase64String($bytes)
    $apiFileUrl = "https://api.github.com/repos/$owner/$RepoName/contents/$relPath"

    $existingSha = $null
    try {
        $existing = Invoke-RestMethod -Uri $apiFileUrl -Headers $headers -Method Get
        $existingSha = $existing.sha
    } catch { }

    $putBody = @{
        message = ("Helios T2 v" + $currentVer + " - Update " + $relPath)
        content = $b64
    }
    if ($existingSha) { $putBody["sha"] = $existingSha }

    $jsonBody = $putBody | ConvertTo-Json -Compress
    $null = Invoke-RestMethod -Uri $apiFileUrl -Headers $headers -Method Put -Body $jsonBody -ContentType "application/json"
    Write-Host "      Uploaded source file: $relPath" -ForegroundColor Gray
}

# Create or Fetch GitHub Release for tag v$currentVer
$tag = "v" + $currentVer
$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$RepoName/releases/tags/$tag" -Headers $headers -Method Get
} catch { }

$assetName = "HeliosT2_Setup_v" + $currentVer + ".exe"

if (-not $release) {
    Write-Host "      Creating GitHub Release $tag..." -ForegroundColor Cyan
    $relMarkdown = "## Helios T2 v" + $currentVer + "`r`n`r`n" + $ReleaseNotes + "`r`n`r`n### Direct Installer Download`r`nDownload **" + $assetName + "** below and run it to install or upgrade."
    $relBody = @{
        tag_name   = $tag
        name       = ("Helios T2 v" + $currentVer)
        body       = $relMarkdown
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$RepoName/releases" -Headers $headers -Method Post -Body $relBody -ContentType "application/json"
}

# Upload Setup .exe Asset to Release
foreach ($existingAsset in $release.assets) {
    if ($existingAsset.name -eq $assetName) {
        Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$RepoName/releases/assets/$($existingAsset.id)" -Headers $headers -Method Delete | Out-Null
    }
}

$uploadUrl = ($release.upload_url -replace '\{\?name,label\}', '') + "?name=" + $assetName
$exeBytes = [System.IO.File]::ReadAllBytes($setupVersionedExe)
$assetResp = Invoke-RestMethod -Uri $uploadUrl -Headers $headers -Method Post -Body $exeBytes -ContentType "application/octet-stream"

Write-Host "=================================================================" -ForegroundColor Green
Write-Host "  PUBLISHED SUCCESSFULLY TO GITHUB!                              " -ForegroundColor Green
Write-Host "  Repository : https://github.com/$owner/$RepoName               " -ForegroundColor Green
Write-Host "  Release    : $($release.html_url)                              " -ForegroundColor Green
Write-Host "  Setup .EXE : $($assetResp.browser_download_url)                " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
