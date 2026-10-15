# Builds the Windows installer (Velopack): self-contained app + NativeAOT host + built extension.
#   pwsh build/pack.ps1 [-Version 0.1.0]
# Output: artifacts/releases/Fluxer-win-Setup.exe (+ update packages for the update feed).
param([string]$Version = "0.3.0")
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

Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { dotnet tool install -g vpk --version 1.2.161 }
vpk pack --packId Fluxer --packVersion $Version --packDir $publish --mainExe Fluxer.exe `
    --packTitle Fluxer --packAuthors Fluxer --icon "$root/src/Fluxer.App/Assets/fluxer.ico" --outputDir $releases
if ($LASTEXITCODE) { throw "vpk pack failed" }
Write-Host "Installer: $releases\Fluxer-win-Setup.exe"
