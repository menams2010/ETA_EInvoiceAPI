<#
.SYNOPSIS
    Publishes the solution's web apps with their Folder publish profiles.

.DESCRIPTION
    Uses Properties/PublishProfiles/FolderProfile-<Arch>.pubxml in each project.
    Output goes to publish\<Arch>\<Project>\ at the repository root; copy each folder to its IIS site.

    x64 is the default. EInvoiceAPI and EInvoiceAPIAdmin load the 32-bit USB token driver
    (eps2003csp11.dll), which a 64-bit process cannot load: use -Arch x86 on servers that sign
    with the token, and set "Enable 32-Bit Applications" = True on those IIS app pools.

.EXAMPLE
    .\publish.ps1                                # all apps, x64
.EXAMPLE
    .\publish.ps1 -Arch x86                      # all apps, x86
.EXAMPLE
    .\publish.ps1 -Project EInvoiceAPI -Arch x86 # one app
#>
param(
    [ValidateSet('x64', 'x86')]
    [string] $Arch = 'x64',

    [ValidateSet('All', 'EInvoiceAPI', 'EInvoiceAPIAdmin', 'IncomeTaxWebAPI')]
    [string] $Project = 'All'
)

$ErrorActionPreference = 'Stop'

$projects = [ordered]@{
    EInvoiceAPI      = 'src\EInvoiceAPI\EInvoiceAPI\EInvoiceAPI.csproj'
    EInvoiceAPIAdmin = 'src\EInvoiceAPIAdmin\EInvoiceAPIAdmin\EInvoiceAPIAdmin.csproj'
    IncomeTaxWebAPI  = 'src\IncomeTaxWebAPI\IncomeTaxWebAPI\IncomeTaxWebAPI.csproj'
}

$selected = if ($Project -eq 'All') { $projects.Keys } else { @($Project) }

foreach ($name in $selected) {
    $csproj = Join-Path $PSScriptRoot $projects[$name]
    Write-Host "Publishing $name ($Arch)..." -ForegroundColor Cyan
    dotnet publish $csproj -c Release "-p:PublishProfile=FolderProfile-$Arch"
    if ($LASTEXITCODE -ne 0) { throw "Publishing $name failed (exit code $LASTEXITCODE)." }
}

Write-Host "Done. Output: $(Join-Path $PSScriptRoot "publish\$Arch")" -ForegroundColor Green
