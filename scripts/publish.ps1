<#
.SYNOPSIS
    Builds Pipes as a single-file executable and names it Pipes.scr, ready to install.

.DESCRIPTION
    Output: publish\Pipes.scr.

    By default the build is self-contained: the .NET runtime is bundled inside the file, so it runs on any 64-bit
    Windows 10/11 with nothing else to install. This is what the GitHub releases ship. It's a bigger file (about
    50 MB), so the bundled files are compressed; they're unpacked into memory at start-up, which adds a moment
    to the launch but nothing once it's running.

    -FrameworkDependent builds the small version instead (a few MB), which needs the .NET 10 Desktop Runtime to be
    installed (a dev machine already has it).

    Install: right-click Pipes.scr -> Install, or run with -Install to copy it to System32 (needs admin),
    which also makes it show up in the Screen Saver Settings dropdown.

.EXAMPLE
    .\scripts\publish.ps1
.EXAMPLE
    .\scripts\publish.ps1 -FrameworkDependent
.EXAMPLE
    .\scripts\publish.ps1 -Install   # from an elevated PowerShell
#>
param([switch]$Install, [switch]$FrameworkDependent)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish'

$selfContained = -not $FrameworkDependent
# Compression only applies to self-contained single files (it compresses the bundled runtime).
$compress = if ($selfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $root 'src\Pipes\Pipes.csproj') -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$compress -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $out 'Pipes.exe') (Join-Path $out 'Pipes.scr') -Force
$size = (Get-Item (Join-Path $out 'Pipes.scr')).Length / 1MB
Write-Host ("Built {0} ({1}, {2:N1} MB)" -f (Join-Path $out 'Pipes.scr'),
    $(if ($selfContained) { 'self-contained' } else { 'needs the .NET 10 Desktop Runtime' }), $size)

if ($Install) {
    Copy-Item (Join-Path $out 'Pipes.scr') "$env:WINDIR\System32\Pipes.scr" -Force
    Write-Host "Copied to $env:WINDIR\System32\Pipes.scr. Pick 'Pipes' in Screen Saver Settings."
}
