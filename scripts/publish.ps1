<#
.SYNOPSIS
    Builds Pipes as a single-file executable and names it Pipes.scr, ready to install.

.DESCRIPTION
    Output: publish\Pipes.scr (needs the .NET 10 Desktop Runtime, which is already installed on a dev machine).
    Install: right-click Pipes.scr -> Install, or run with -Install to copy it to System32 (needs admin),
    which also makes it show up in the Screen Saver Settings dropdown.

.EXAMPLE
    .\scripts\publish.ps1
.EXAMPLE
    .\scripts\publish.ps1 -Install   # from an elevated PowerShell
#>
param([switch]$Install)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish'

dotnet publish (Join-Path $root 'src\Pipes\Pipes.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $out 'Pipes.exe') (Join-Path $out 'Pipes.scr') -Force
Write-Host "Built $(Join-Path $out 'Pipes.scr')"

if ($Install) {
    Copy-Item (Join-Path $out 'Pipes.scr') "$env:WINDIR\System32\Pipes.scr" -Force
    Write-Host "Copied to $env:WINDIR\System32\Pipes.scr. Pick 'Pipes' in Screen Saver Settings."
}
