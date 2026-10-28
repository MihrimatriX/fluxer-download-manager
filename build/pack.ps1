# Builds the Windows installer (Velopack): self-contained app + NativeAOT host + built extension (Chromium and Firefox).
#   pwsh build/pack.ps1 [-Version 0.1.0]
# Output: artifacts/releases/Fluxer-win-Setup.exe (+ update packages for the update feed).
param([string]$Version = ([xml](Get-Content "$PSScriptRoot/../Directory.Build.props")).Project.PropertyGroup.Version)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts/publish"
$releases = Join-Path $root "artifacts/releases"

# The ILCompiler's MSVC discovery needs vswhere on PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root/src/Fluxer.App" -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw "app publish failed" }
dotnet publish "$root/src/Fluxer.NativeHost" -c Release -r win-x64 -p:Version=$Version -o "$publish/hosttmp"
if ($LASTEXITCODE) { throw "host publish failed" }
Move-Item "$publish/hosttmp/Fluxer.NativeHost.exe" $publish -Force
Remove-Item "$publish/hosttmp" -Recurse -Force

Push-Location "$root/extension"
npm ci --no-audit --no-fund
npm run build
if ($LASTEXITCODE) { Pop-Location; throw "extension build failed" }
Pop-Location
Copy-Item "$root/extension/dist" "$publish/extension" -Recurse -Force
Copy-Item "$root/extension/dist-firefox" "$publish/extension-firefox" -Recurse -Force

Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { dotnet tool install -g vpk --version 1.2.161 }
# Code signing (optional; unsigned builds still work but hit SmartScreen). vpk signs every exe/dll in
# the pack dir (Fluxer.exe, Fluxer.NativeHost.exe, ...) and then the installer itself.
#   Azure Artifact Signing: $env:FLUXER_AZURE_SIGN_FILE = path to metadata.json
#                           ({"Endpoint": "...", "CodeSigningAccountName": "...", "CertificateProfileName": "..."}),
#                           credentials from the environment (AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_CLIENT_SECRET).
#   Certificate + signtool: $env:FLUXER_SIGN_PARAMS = '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p ...'
$sign = @()
if ($env:FLUXER_AZURE_SIGN_FILE) { $sign = @('--azureTrustedSignFile', $env:FLUXER_AZURE_SIGN_FILE) }
elseif ($env:FLUXER_SIGN_PARAMS) { $sign = @('--signParams', $env:FLUXER_SIGN_PARAMS) }
else { Write-Warning "No signing configured: the installer will be unsigned." }

vpk pack --packId Fluxer --packVersion $Version --packDir $publish --mainExe Fluxer.exe `
    --packTitle Fluxer --packAuthors Fluxer --icon "$root/src/Fluxer.App/Assets/fluxer.ico" --outputDir $releases @sign
if ($LASTEXITCODE) { throw "vpk pack failed" }
Write-Host "Installer: $releases\Fluxer-win-Setup.exe"
