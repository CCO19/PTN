$ErrorActionPreference = "Stop"

[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$project = Join-Path $PSScriptRoot "PTN.csproj"
$icon = Join-Path $PSScriptRoot "Assets\PTN.icns"
$publishRoot = Join-Path $PSScriptRoot "publish"

# Nettoyage complet avant publication
$binRoot = Join-Path $PSScriptRoot "bin"
$objRoot = Join-Path $PSScriptRoot "obj"

Write-Host "Nettoyage de bin et obj..." -ForegroundColor Yellow

if (Test-Path $binRoot) {
    Remove-Item $binRoot -Recurse -Force
}

if (Test-Path $objRoot) {
    Remove-Item $objRoot -Recurse -Force
}

Write-Host "dotnet clean..." -ForegroundColor Yellow

dotnet clean $project --configuration Release

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "ECHEC : dotnet clean" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Publication de PTN" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $project)) {
    throw "Projet introuvable : $project"
}

if (-not (Test-Path $icon)) {
    throw "Icône macOS introuvable : $icon"
}

$profiles = @(
    "win-x64",
    "linux-x64",
    "osx-x64",
    "osx-arm64"
)

if (Test-Path $publishRoot) {
    Write-Host "Nettoyage de l'ancien dossier publish..." -ForegroundColor Yellow
    Remove-Item $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

foreach ($profile in $profiles) {

    Write-Host ""
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host " Publication : $profile" -ForegroundColor Yellow
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host ""

    $profilePublish = Join-Path $publishRoot $profile

    New-Item -ItemType Directory -Path $profilePublish -Force | Out-Null

    dotnet publish $project `
        --configuration Release `
        --runtime $profile `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PublishDir="$profilePublish\"

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "ECHEC : $profile" -ForegroundColor Red
        exit $LASTEXITCODE
    }

    if ($profile -eq "osx-x64" -or $profile -eq "osx-arm64") {

        $binary = Join-Path $profilePublish "PTN"
        $app = Join-Path $profilePublish "PTN.app"
        $contents = Join-Path $app "Contents"
        $macos = Join-Path $contents "MacOS"
        $resources = Join-Path $contents "Resources"

        if (-not (Test-Path $binary)) {
            throw "Binaire macOS introuvable : $binary"
        }

        Write-Host "Création de PTN.app..." -ForegroundColor Yellow

        New-Item -ItemType Directory -Path $macos -Force | Out-Null
        New-Item -ItemType Directory -Path $resources -Force | Out-Null

        Move-Item $binary (Join-Path $macos "PTN") -Force

        Copy-Item $icon (Join-Path $resources "PTN.icns") -Force

        $plistPath = Join-Path $contents "Info.plist"

        $plist = @(
            '<?xml version="1.0" encoding="UTF-8"?>'
            '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">'
            '<plist version="1.0">'
            '<dict>'
            '    <key>CFBundleDisplayName</key>'
            "    <string>Pas l'Temps d'Niaiser !</string>"
            '    <key>CFBundleExecutable</key>'
            '    <string>PTN</string>'
            '    <key>CFBundleIdentifier</key>'
            '    <string>com.ptn.app</string>'
            '    <key>CFBundleName</key>'
            '    <string>PTN</string>'
            '    <key>CFBundlePackageType</key>'
            '    <string>APPL</string>'
            '    <key>CFBundleSignature</key>'
            '    <string>PTN1</string>'
            '    <key>CFBundleIconFile</key>'
            '    <string>PTN.icns</string>'
            '    <key>CFBundleVersion</key>'
            '    <string>1.0.0</string>'
            '    <key>CFBundleShortVersionString</key>'
            '    <string>1.0.0</string>'
            '    <key>LSMinimumSystemVersion</key>'
            '    <string>11.0</string>'
            '</dict>'
            '</plist>'
        )

        Set-Content -Path $plistPath -Value $plist -Encoding UTF8

        Write-Host "PTN.app créée." -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "OK : $profile" -ForegroundColor Green
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Publication terminée" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

Get-ChildItem $publishRoot -Directory |
    ForEach-Object {

        Write-Host ""
        Write-Host $_.Name -ForegroundColor Cyan

        Get-ChildItem $_.FullName -Recurse -Force |
            Where-Object { -not $_.PSIsContainer } |
            ForEach-Object {

                $size = [math]::Round($_.Length / 1MB, 1)

                Write-Host "  $($_.FullName) - $size Mo"
            }
    }

Write-Host ""
Write-Host "Les publications sont disponibles dans :" -ForegroundColor Cyan
Write-Host "  $publishRoot" -ForegroundColor White
Write-Host ""
