<#
.SYNOPSIS
  Builds an MSIX package of D2 Chat Translator (specification 6.2, 17.3).

.DESCRIPTION
  1. dotnet publish (self-contained, win-x64, deterministic CI build) into artifacts\msix\layout
  2. Adds LICENSE, CHANGELOG.md, THIRD-PARTY-NOTICES.md and sbom.cdx.json (scripts\generate-sbom.ps1) to the layout
  3. Copies packaging\AppxManifest.xml and generates placeholder logos
  4. makeappx pack -> artifacts\msix\D2ChatTranslator_<version>_x64.msix
  5. Signs the package when -CertificateThumbprint or -PfxPath is given
  6. Writes the SHA-256 of the package next to it (<package>.sha256)

  The manifest Publisher must equal the Subject of the signing certificate. When a certificate is
  given and -Publisher is not, the Subject is read from the certificate.

  An unsigned package cannot be installed. For local development with package identity,
  register the layout folder instead (requires Developer Mode, which you enable yourself):

    Add-AppxPackage -Register artifacts\msix\layout\AppxManifest.xml

  This script never creates certificates, changes certificate trust, or changes Windows settings.
  See docs/distribution.md for how to obtain and trust a signing certificate.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\package-msix.ps1

.EXAMPLE
  powershell -File scripts\package-msix.ps1 -CertificateThumbprint 0123ABCD...

.EXAMPLE
  powershell -File scripts\package-msix.ps1 -PfxPath C:\keys\signing.pfx -PfxPassword (Read-Host -AsSecureString)
#>
param(
    [string] $Configuration = "Release",
    # Four-part package version. Defaults to <Version> in Directory.Build.props plus ".0".
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version,
    # Must match the Subject of the signing certificate.
    [string] $Publisher = "CN=D2ChatTranslator Dev",
    # A code signing certificate in Cert:\CurrentUser\My.
    [string] $CertificateThumbprint,
    # Or a PFX file with its password.
    [string] $PfxPath,
    [SecureString] $PfxPassword,
    # RFC 3161 time stamp server. Pass "" to sign without a time stamp (the signature then expires with the certificate).
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw -Encoding UTF8
    $declared = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if ($declared -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no three-part <Version>." }
    $Version = "$declared.0"
}
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

function Get-PlainText([SecureString] $secure) {
    if (-not $secure) { return "" }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if ($CertificateThumbprint -and $PfxPath) { throw "Pass either -CertificateThumbprint or -PfxPath, not both." }

# The manifest Publisher must equal the certificate Subject
if (-not $PSBoundParameters.ContainsKey('Publisher')) {
    if ($CertificateThumbprint) {
        $cert = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
        $Publisher = $cert.Subject
    }
    elseif ($PfxPath) {
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (Resolve-Path $PfxPath).Path, (Get-PlainText $PfxPassword)
        $Publisher = $cert.Subject
        $cert.Dispose()
    }
}
Write-Host "Publisher: $Publisher"

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $assets | Out-Null

Write-Host "Publishing ($Configuration, win-x64, self-contained)..."
$plainVersion = $Version -replace '\.\d+$', ''
dotnet publish (Join-Path $root "src\D2Translator.App\D2Translator.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $layout -p:Version=$plainVersion -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# License, third-party notices and SBOM ship inside the package (specification 17.3)
Copy-Item (Join-Path $root "LICENSE") (Join-Path $layout "LICENSE.txt")
# Changes, supported configuration and open issues for each version (specification 17.3)
Copy-Item (Join-Path $root "CHANGELOG.md") (Join-Path $layout "CHANGELOG.md")
& (Join-Path $PSScriptRoot "generate-sbom.ps1") -PublishDir $layout -OutDir $layout -AppVersion $plainVersion
Copy-Item (Join-Path $layout "THIRD-PARTY-NOTICES.md"), (Join-Path $layout "sbom.cdx.json") $outDir -Force

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

if ($CertificateThumbprint -or $PfxPath) {
    $signtool = Find-SdkTool "signtool.exe"
    $arguments = @("sign", "/fd", "SHA256")
    if ($CertificateThumbprint) { $arguments += @("/sha1", $CertificateThumbprint) }
    else { $arguments += @("/f", (Resolve-Path $PfxPath).Path, "/p", (Get-PlainText $PfxPassword)) }
    if ($TimestampUrl) { $arguments += @("/tr", $TimestampUrl, "/td", "SHA256") }
    & $signtool @arguments $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed." }
    Write-Host "Signed: $msix"
}
else {
    Write-Host "Created unsigned package: $msix"
    Write-Host "It cannot be installed until signed. See docs/distribution.md."
}

$hash = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$msix.sha256", "$hash  $(Split-Path $msix -Leaf)`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "SHA-256: $hash"
