<#
.SYNOPSIS
  Builds a release: a signed MSIX with a one-click installer for friends, plus a portable zip.

.DESCRIPTION
  Output in artifacts\release:
    MoonMovie-<version>-x64-安装包.zip   MSIX + certificate + 「安装 MoonMovie.cmd」
    MoonMovie-<version>-x64-便携版.zip   unpackaged, unzip and run MoonMovie.exe
    MoonMovie-<version>-x64.msix         the signed package alone, for in-app updates

  Signing uses build\cert\MoonMovie.pfx (create it once with build\new-cert.ps1); the password comes from
  -Password, $env:SIGNING_PFX_PASSWORD or build\cert\password.txt. Nothing touches the certificate store.

  A .env at the repository root (TMDB key, danmaku server) is packaged too: keys are then readable by anyone who
  has the package. Leave it out for public builds, and users enter their own keys in Settings.

.EXAMPLE
  .\build\package.ps1 -Version 1.0.0
#>
param(
    [string] $Version = '1.0.0',
    [string] $Pfx = (Join-Path $PSScriptRoot 'cert\MoonMovie.pfx'),
    [string] $Password,
    [switch] $SkipPortable
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\MoonMovie\MoonMovie.csproj'
$manifest = Join-Path $root 'src\MoonMovie\Package.appxmanifest'
$out = Join-Path $root 'artifacts\release'

# Four-part package version ("1.2.3" → "1.2.3.0").
$parts = @($Version.TrimStart('v').Split('.') | ForEach-Object { [int]$_ })
while ($parts.Count -lt 4) { $parts += 0 }
$packageVersion = ($parts[0..3] -join '.')
$label = ($parts[0..2] -join '.')

if (-not $Password) { $Password = $env:SIGNING_PFX_PASSWORD }
if (-not $Password -and (Test-Path (Join-Path $PSScriptRoot 'cert\password.txt'))) { $Password = Get-Content (Join-Path $PSScriptRoot 'cert\password.txt') -Raw }
if (-not (Test-Path $Pfx)) { throw "Signing certificate not found: $Pfx (run build\new-cert.ps1 first)." }
if (-not $Password) { throw 'No certificate password (-Password, SIGNING_PFX_PASSWORD or build\cert\password.txt).' }

& (Join-Path $PSScriptRoot 'get-libmpv.ps1')
if (Test-Path (Join-Path $root '.env')) { Write-Warning '.env will be packaged: its keys ship with this build.' }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

# ----- MSIX ---------------------------------------------------------------------------------------------------
$original = [IO.File]::ReadAllText($manifest)
try {
    [IO.File]::WriteAllText($manifest, ($original -replace '(<Identity[^>]*Version=")[^"]+(")', "`${1}$packageVersion`${2}"), [Text.UTF8Encoding]::new($false))
    $msixDir = Join-Path $out '_msix\'
    dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:PublishTrimmed=false `
        -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=$msixDir `
        -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false -p:Version=$label -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed.' }
}
finally {
    [IO.File]::WriteAllText($manifest, $original, [Text.UTF8Encoding]::new($false))
}

$msix = Get-ChildItem $msixDir -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $msix) { throw 'No .msix produced.' }

$signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools", (dotnet nuget locals global-packages -l | ForEach-Object { ($_ -split ': ', 2)[1] } | ForEach-Object { Join-Path $_ 'microsoft.windows.sdk.buildtools' }) `
    -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object FullName -match '\\x64\\' | Select-Object -Last 1
if (-not $signtool) { $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object FullName -match '\\x64\\' | Select-Object -Last 1 }
if (-not $signtool) { throw 'signtool.exe not found (Windows SDK build tools).' }
& $signtool.FullName sign /fd SHA256 /f $Pfx /p $Password $msix.FullName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }

$kit = Join-Path $out "MoonMovie-$label"
New-Item -ItemType Directory -Force $kit | Out-Null
Copy-Item $msix.FullName (Join-Path $kit "MoonMovie-$label-x64.msix")
# The bare package too: installed copies download it to update themselves (Settings → 软件更新).
Copy-Item $msix.FullName (Join-Path $out "MoonMovie-$label-x64.msix")
Copy-Item ([IO.Path]::ChangeExtension($Pfx, '.cer')) (Join-Path $kit 'MoonMovie.cer')
Copy-Item (Join-Path $PSScriptRoot 'installer\*') $kit
Compress-Archive -Path "$kit\*" -DestinationPath (Join-Path $out "MoonMovie-$label-x64-安装包.zip") -CompressionLevel Optimal
Remove-Item (Join-Path $out '_msix') -Recurse -Force

# ----- Portable -----------------------------------------------------------------------------------------------
if (-not $SkipPortable) {
    $portable = Join-Path $out '_portable'
    dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:PublishTrimmed=false -p:Version=$label -o $portable -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }
    Compress-Archive -Path "$portable\*" -DestinationPath (Join-Path $out "MoonMovie-$label-x64-便携版.zip") -CompressionLevel Optimal
    Remove-Item $portable -Recurse -Force
}

Get-ChildItem $out -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
