<#
.SYNOPSIS
  Builds the Microsoft Store MSIX package from a self-contained `dotnet publish` output.

.DESCRIPTION
  The package is left unsigned: Partner Center accepts unsigned uploads and Microsoft signs the
  package after certification. To install it locally for testing, sign a copy with a test
  certificate whose subject matches -Publisher (see packaging/windows/msix/README.md).

.EXAMPLE
  ./build-msix.ps1 -Version 0.8.13 -PublishDir C:\src\Capture\publish `
    -IdentityName Fybre.Capture -Publisher "CN=00000000-0000-0000-0000-000000000000" -PublisherDisplayName Fybre
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $PublishDir,
  [Parameter(Mandatory)] [string] $IdentityName,
  [Parameter(Mandatory)] [string] $Publisher,
  [Parameter(Mandatory)] [string] $PublisherDisplayName,
  [string] $OutputDir = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Stop'

# MSIX versions are four numeric parts and the Store requires the last to be 0. Release tags give
# "0.8.13"; CI builds give "0.1.0-ci.42" — keep only the numeric major.minor.build.
$numeric = ($Version -split '[-+]')[0]
$parts = @($numeric.Split('.') | ForEach-Object { [int]$_ })
while ($parts.Count -lt 3) { $parts += 0 }
$packageVersion = "{0}.{1}.{2}.0" -f $parts[0], $parts[1], $parts[2]

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
  Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } |
  Sort-Object { [version]$_.Name } -Descending |
  Select-Object -First 1
if (-not $sdkBin) { throw 'makeappx.exe not found — install the Windows 10/11 SDK.' }
$makeAppx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$makePri = Join-Path $sdkBin.FullName 'x64\makepri.exe'

$staging = Join-Path ([IO.Path]::GetTempPath()) "capture-msix-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging | Out-Null
try {
  Copy-Item -Path (Join-Path $PublishDir '*') -Destination $staging -Recurse
  Copy-Item -Path (Join-Path $PSScriptRoot 'Assets') -Destination (Join-Path $staging 'Assets') -Recurse -Force

  $escape = { param($value) [Security.SecurityElement]::Escape($value) }
  $manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
  $manifest = $manifest.Replace('__IDENTITY_NAME__', (& $escape $IdentityName)).
    Replace('__PUBLISHER_DISPLAY_NAME__', (& $escape $PublisherDisplayName)).
    Replace('__PUBLISHER__', (& $escape $Publisher)).
    Replace('__VERSION__', $packageVersion)
  if ($manifest -match '__[A-Z_]+__') { throw "Unreplaced manifest token: $($Matches[0])" }
  Set-Content -Path (Join-Path $staging 'AppxManifest.xml') -Value $manifest -Encoding utf8

  # Index only the logos (not the whole publish output) into resources.pri, so Windows can pick the
  # right scale and the unplated taskbar/Start icons. Without it the logos get an accent-colour backing.
  $priRoot = Join-Path ([IO.Path]::GetTempPath()) "capture-pri-$([guid]::NewGuid().ToString('N'))"
  New-Item -ItemType Directory -Path $priRoot | Out-Null
  try {
    Copy-Item -Path (Join-Path $PSScriptRoot 'Assets') -Destination (Join-Path $priRoot 'Assets') -Recurse
    Copy-Item -Path (Join-Path $staging 'AppxManifest.xml') -Destination $priRoot
    & $makePri new /pr $priRoot /cf (Join-Path $PSScriptRoot 'priconfig.xml') `
      /mn (Join-Path $priRoot 'AppxManifest.xml') /of (Join-Path $staging 'resources.pri') /o
    if ($LASTEXITCODE -ne 0) { throw "makepri failed with exit code $LASTEXITCODE" }
  }
  finally {
    Remove-Item -Path $priRoot -Recurse -Force -ErrorAction SilentlyContinue
  }

  New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
  $output = Join-Path $OutputDir "Capture-$packageVersion-x64.msix"
  & $makeAppx pack /d $staging /p $output /o
  if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }
  Write-Host "Built $output"
}
finally {
  Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
}
