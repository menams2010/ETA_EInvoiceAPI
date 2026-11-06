<#
.SYNOPSIS
    Creates the IIS layout for the ETA e-invoicing apps (run as Administrator on the IIS server).

.DESCRIPTION
    URLs (http or https, depending on the site's bindings):
        http(s)://<host>/EInvoiceAPI                    -> EInvoiceAPI
        http(s)://<host>/EInvoiceAPI/Admin              -> EInvoiceAPIAdmin
        http(s)://<host>/EInvoiceAPI/ETANotifications   -> IncomeTaxWebAPI

    Physical folders are siblings (not nested) so republishing one app never deletes another:
        <WebRoot>\EInvoiceAPI, <WebRoot>\Admin, <WebRoot>\ETANotifications

    Each app gets its own app pool (required for ASP.NET Core in-process hosting), set to
    No Managed Code. With -Arch x86 the pools run 32-bit, which the USB token driver
    (eps2003csp11.dll) needs.

    EInvoiceAPI and Admin share config.db: the script sets Database__Path and Database__Password
    on both pools and gives both pool identities Modify rights on the data folder.

    Safe to re-run: existing pools/applications are updated, not duplicated.
    Prerequisite: ASP.NET Core 10.0 Hosting Bundle installed.

.EXAMPLE
    .\Setup-IIS.ps1                                  # Default Web Site, x64
.EXAMPLE
    .\Setup-IIS.ps1 -Arch x86 -SiteName "ETA"        # 32-bit pools (USB token signing)
#>
#Requires -RunAsAdministrator
param(
    [string] $SiteName = 'Default Web Site',

    # Parent folder for the three app folders.
    [string] $WebRoot = 'C:\inetpub\EInvoiceAPI',

    # Must match the publish profile used (FolderProfile-x64 / FolderProfile-x86).
    [ValidateSet('x64', 'x86')]
    [string] $Arch = 'x64',

    # Shared LiteDB file; keep it outside the web folders.
    [string] $DatabasePath = 'C:\ETA\Data\config.db',

    # Folder IncomeTaxWebAPI writes its SQLite logs to (hard-coded in DBManager.cs).
    [string] $NotificationsDataFolder = 'C:\EInvoice',

    # Same value for EInvoiceAPI and Admin; at least 32 characters. Prompted for if omitted.
    [securestring] $DatabasePassword
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

if (-not (Test-Path "IIS:\Sites\$SiteName")) { throw "IIS site '$SiteName' not found." }

if (-not $DatabasePassword) {
    $DatabasePassword = Read-Host -AsSecureString 'Database password for config.db (same for API and Admin, 32+ characters)'
}
$plainPassword = [System.Net.NetworkCredential]::new('', $DatabasePassword).Password
if ($plainPassword.Length -lt 32) { throw 'The database password must be at least 32 characters.' }

$apps = @(
    @{ Pool = 'EInvoiceAPI';                  Folder = 'EInvoiceAPI';      Parent = $SiteName;                 Name = 'EInvoiceAPI';      Url = '/EInvoiceAPI';                  UsesConfigDb = $true  }
    @{ Pool = 'EInvoiceAPI-Admin';            Folder = 'Admin';            Parent = "$SiteName\EInvoiceAPI";   Name = 'Admin';            Url = '/EInvoiceAPI/Admin';            UsesConfigDb = $true  }
    @{ Pool = 'EInvoiceAPI-ETANotifications'; Folder = 'ETANotifications'; Parent = "$SiteName\EInvoiceAPI";  Name = 'ETANotifications'; Url = '/EInvoiceAPI/ETANotifications'; UsesConfigDb = $false }
)

function Set-PoolEnvironmentVariable([string] $pool, [string] $name, [string] $value) {
    $filter = "system.applicationHost/applicationPools/add[@name='$pool']/environmentVariables"
    Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' `
        -AtElement @{ name = $name } -ErrorAction SilentlyContinue
    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' `
        -Value @{ name = $name; value = $value }
}

function Grant-Modify([string] $path, [string] $pool) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    & icacls $path /grant "IIS AppPool\$($pool):(OI)(CI)M" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $path" }
}

$dataFolder = Split-Path -Parent $DatabasePath

foreach ($app in $apps) {
    $physicalPath = Join-Path $WebRoot $app.Folder
    New-Item -ItemType Directory -Force -Path $physicalPath | Out-Null

    # App pool
    if (-not (Test-Path "IIS:\AppPools\$($app.Pool)")) { New-WebAppPool -Name $app.Pool | Out-Null }
    $poolPath = "IIS:\AppPools\$($app.Pool)"
    Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''          # No Managed Code
    Set-ItemProperty $poolPath -Name enable32BitAppOnWin64 -Value ($Arch -eq 'x86')
    Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
    Set-PoolEnvironmentVariable $app.Pool 'ASPNETCORE_ENVIRONMENT' 'Production'

    if ($app.UsesConfigDb) {
        Set-PoolEnvironmentVariable $app.Pool 'Database__Path' $DatabasePath
        Set-PoolEnvironmentVariable $app.Pool 'Database__Password' $plainPassword
        Grant-Modify $dataFolder $app.Pool
    }
    else {
        Grant-Modify $NotificationsDataFolder $app.Pool
    }

    # Application (create, or point an existing one at the right folder and pool)
    $appPath = "IIS:\Sites\$($app.Parent)\$($app.Name)"
    if (Test-Path $appPath) {
        Set-ItemProperty $appPath -Name physicalPath -Value $physicalPath
        Set-ItemProperty $appPath -Name applicationPool -Value $app.Pool
    }
    else {
        New-WebApplication -Site $app.Parent -Name $app.Name -PhysicalPath $physicalPath -ApplicationPool $app.Pool | Out-Null
    }

    Write-Host ("{0,-30} -> {1}  (pool {2}, {3})" -f $app.Url, $physicalPath, $app.Pool, $Arch) -ForegroundColor Green
}

$plainPassword = $null

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Copy publish\$Arch\EInvoiceAPI, publish\$Arch\EInvoiceAPIAdmin and publish\$Arch\IncomeTaxWebAPI"
Write-Host "     into $WebRoot\EInvoiceAPI, $WebRoot\Admin and $WebRoot\ETANotifications (see docs\IIS_DEPLOYMENT.md)."
Write-Host '  2. First install with an empty database only: set Bootstrap__Username / Bootstrap__Password on the'
Write-Host '     EInvoiceAPI-Admin pool, start it once, then remove them.'
Write-Host '  3. Update the notification URL registered in the ETA portal and, if Microsoft mail is used, the'
Write-Host '     redirect URI in the Entra app registration (see docs\IIS_DEPLOYMENT.md).'
