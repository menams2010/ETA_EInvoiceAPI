<#
.SYNOPSIS
    Builds artifacts\EInvoiceAPI-Setup-<Version>.exe (run on Windows).

.DESCRIPTION
    1. Publishes all apps for x64 and x86 (publish.ps1).
    2. Publishes ETA.SetupTool and zips it (publish\SetupTool.zip).
    3. Compiles installer\EInvoiceAPI-Setup.iss with Inno Setup 6 (ISCC.exe).

    Needs the .NET 10 SDK and Inno Setup 6 (https://jrsoftware.org/isdl.php, or: winget install JRSoftware.InnoSetup).

.EXAMPLE
    .\installer\build-installer.ps1 -Version 1.2.0
#>
param(
    [string] $Version = '1.0.0',
    # Path to ISCC.exe; found automatically when Inno Setup 6 is installed in the default location.
    [string] $Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Checked([string] $what, [scriptblock] $command) {
    Write-Host "== $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

Invoke-Checked 'Publish apps (x64)' { & (Join-Path $root 'publish.ps1') -Arch x64 }
Invoke-Checked 'Publish apps (x86)' { & (Join-Path $root 'publish.ps1') -Arch x86 }
Invoke-Checked 'Publish ETA.SetupTool' {
    $toolDir = Join-Path $root 'publish\SetupTool'
    if (Test-Path $toolDir) { Remove-Item $toolDir -Recurse -Force }
    dotnet publish (Join-Path $root 'installer\ETA.SetupTool\ETA.SetupTool.csproj') -c Release -r win-x64 --self-contained false -o $toolDir
}
# The installer carries the helper as one zip and unpacks it into its temp folder when needed.
Get-ChildItem (Join-Path $root 'publish\SetupTool') -Filter *.pdb | Remove-Item
Compress-Archive -Path (Join-Path $root 'publish\SetupTool\*') -DestinationPath (Join-Path $root 'publish\SetupTool.zip') -Force

if (-not $Iscc) {
    $Iscc = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) |
        Where-Object { $_ } |
        ForEach-Object { Get-ChildItem -Path $_ -Filter 'Inno Setup *' -Directory -ErrorAction SilentlyContinue } |
        ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
        Where-Object { Test-Path $_ } |
        Sort-Object -Descending | Select-Object -First 1
    if (-not $Iscc) { $Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    if (-not $Iscc) { throw 'Inno Setup (ISCC.exe) not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc.' }
}

Invoke-Checked 'Compile installer' { & $Iscc "/DAppVersion=$Version" (Join-Path $root 'installer\EInvoiceAPI-Setup.iss') }

Get-Item (Join-Path $root "artifacts\EInvoiceAPI-Setup-$Version.exe") | ForEach-Object {
    Write-Host ("Built {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green
}
