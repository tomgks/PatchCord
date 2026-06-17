# Builds the release artifact: a single, self-contained PatchCord.exe
# that needs no separate .NET install. Output: .\publish\PatchCord.exe
#
# Requires the .NET 10 SDK (https://dotnet.microsoft.com/download). If `dotnet`
# isn't on PATH but you installed it per-user, this script will find ~/.dotnet.

[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutDir  = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'

# Locate the SDK.
$userDotnet = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) { $env:PATH = "$userDotnet;$env:PATH" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download"
}

$proj = Join-Path $PSScriptRoot 'src\PatchCord.csproj'

Write-Host "Publishing $Runtime self-contained single-file exe..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -o $OutDir

$exe = Join-Path $OutDir 'PatchCord.exe'
if (Test-Path $exe) {
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "`nDone -> $exe  ($mb MB)" -ForegroundColor Green
} else {
    throw "Publish finished but $exe was not produced."
}
