<#
.SYNOPSIS
  Puts the pinned libmpv build at lib\mpv\libmpv-2.dll (the project copies it next to the exe).

.DESCRIPTION
  shinchiro/mpv-winbuild-cmake, generic x86_64 dev package, mpv v0.41.0-1092 (client API 2.5). This exact build
  is the one MoonMovie is tested with (composition swap chain, d3d11vpp RTX VSR, gpu-next). Needs 7-Zip.
#>
param(
    [string] $Url = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261002/mpv-dev-x86_64-20261002-git-3186d369f9.7z',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'lib\mpv'
$dll = Join-Path $target 'libmpv-2.dll'
if ((Test-Path $dll) -and -not $Force) { "libmpv already present: $dll"; return }

$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) { $sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $sevenZip) { throw '7-Zip is needed to unpack libmpv (https://www.7-zip.org).' }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('libmpv-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $temp | Out-Null
try {
    $archive = Join-Path $temp 'mpv-dev.7z'
    "Downloading $Url"
    Invoke-WebRequest $Url -OutFile $archive -UseBasicParsing
    & $sevenZip x $archive "-o$temp\x" -y | Out-Null
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Get-ChildItem "$temp\x" -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1).FullName $dll -Force
    "libmpv ready: $dll"
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
