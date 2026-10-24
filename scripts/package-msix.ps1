<#
.SYNOPSIS
  Builds an MSIX package of D2 Chat Translator (specification 6.2).

.DESCRIPTION
  1. dotnet publish (self-contained, win-x64) into artifacts\msix\layout
  2. Copies packaging\AppxManifest.xml and generates placeholder logos
  3. makeappx pack -> artifacts\msix\D2ChatTranslator_<version>_x64.msix
  4. Signs the package only when -CertificateThumbprint is given

  An unsigned package cannot be installed. For local development with package identity,
  register the layout folder instead (requires Developer Mode, which you enable yourself):

    Add-AppxPackage -Register artifacts\msix\layout\AppxManifest.xml

  This script never creates certificates, changes certificate trust, or changes Windows settings.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\package-msix.ps1

.EXAMPLE
  powershell -File scripts\package-msix.ps1 -Publisher "CN=Your Name" -CertificateThumbprint 0123ABCD...
#>
param(
    [string] $Configuration = "Release",
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version = "0.1.0.0",
    # Must match the Subject of the signing certificate.
    [string] $Publisher = "CN=D2ChatTranslator Dev",
    [string] $CertificateThumbprint
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "artifacts\msix"
$layout = Join-Path $outDir "layout"
$assets = Join-Path $layout "Assets"
$msix = Join-Path $outDir "D2ChatTranslator_${Version}_x64.msix"

function Find-SdkTool([string] $name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $tool = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $tool) { throw "$name was not found. Install the Windows SDK." }
    return $tool
}

function New-Logo([string] $path, [int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 32, 40, 56))
        $font = New-Object System.Drawing.Font "Segoe UI", ([single]($size * 0.36)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $format = New-Object System.Drawing.StringFormat
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
        $graphics.DrawString("D2", $font, [System.Drawing.Brushes]::White, $rect, $format)
        $font.Dispose()
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $assets | Out-Null

Write-Host "Publishing ($Configuration, win-x64, self-contained)..."
dotnet publish (Join-Path $root "src\D2Translator.App\D2Translator.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $layout -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$manifest = Get-Content (Join-Path $root "packaging\AppxManifest.xml") -Raw -Encoding UTF8
$manifest = $manifest.Replace('$(Version)', $Version).Replace('$(Publisher)', [System.Security.SecurityElement]::Escape($Publisher))
[System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding $false))

New-Logo (Join-Path $assets "StoreLogo.png") 50
New-Logo (Join-Path $assets "Square44x44Logo.png") 44
New-Logo (Join-Path $assets "Square150x150Logo.png") 150

$makeappx = Find-SdkTool "makeappx.exe"
Write-Host "Packing with $makeappx"
& $makeappx pack /o /d $layout /p $msix
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed." }

if ($CertificateThumbprint) {
    $signtool = Find-SdkTool "signtool.exe"
    & $signtool sign /fd SHA256 /sha1 $CertificateThumbprint /tr http://timestamp.digicert.com /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed." }
    Write-Host "Signed: $msix"
}
else {
    Write-Host "Created unsigned package: $msix"
    Write-Host "It cannot be installed until signed. See the script help for development registration."
}
