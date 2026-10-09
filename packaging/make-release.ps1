# Builds the release artifacts of PrivateNetRouting into artifacts/: a self-contained publish
# of both projects (the .NET runtime is bundled), the portable zip and the Inno Setup
# installer. The GitHub Actions release workflow runs this same script on a version tag.
# Usage: pwsh -NoProfile -File packaging/make-release.ps1 [-IsccPath <path>]

param(
    # Explicit path to ISCC.exe; the standard Inno Setup 6 locations are probed by default.
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot "artifacts"
$staging = Join-Path $artifacts "staging"

# Directory.Build.props is the single source of the version (the release tag must match it).
$props = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Raw
$version = [regex]::Match($props, "<Version>([^<]+)</Version>").Groups[1].Value
if (-not $version)
{
    throw "Version not found in Directory.Build.props"
}

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Both exes live in one directory: the GUI and pnr.exe share the bundled runtime.
foreach ($project in @("PrivateNetRouting.App", "PrivateNetRouting.Cli"))
{
    dotnet publish (Join-Path $repoRoot "src/$project") -c Release -r win-x64 --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed for $project"
    }
}

$zip = Join-Path $artifacts "PrivateNetRouting-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip -Force

$iscc = if ($IsccPath -ne "")
{
    $IsccPath
}
else
{
    @(
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc)
{
    throw "ISCC.exe not found: install Inno Setup 6 or pass -IsccPath"
}

& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot "setup.iss")
if ($LASTEXITCODE -ne 0)
{
    throw "ISCC failed"
}

Get-ChildItem $artifacts -File | Select-Object Name, @{ Name = "MB"; Expression = { [math]::Round($_.Length / 1MB, 1) } }
