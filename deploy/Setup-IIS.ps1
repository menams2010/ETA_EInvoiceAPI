<#
.SYNOPSIS
    Creates or updates the IIS layout for the ETA e-invoicing apps. Run as Administrator on the IIS server.
    EInvoiceAPI-Setup.exe runs this script too; it can also be run by hand.

.DESCRIPTION
    URLs (http or https, depending on the site's bindings), with the default -BasePath EInvoiceAPI:
        http(s)://<host>/EInvoiceAPI                    -> EInvoiceAPI
        http(s)://<host>/EInvoiceAPI/Admin              -> EInvoiceAPIAdmin
        http(s)://<host>/EInvoiceAPI/ETANotifications   -> IncomeTaxWebAPI (ETA notifications)

    Physical folders are siblings under -WebRoot (not nested), so replacing one app never deletes another:
        <WebRoot>\EInvoiceAPI, <WebRoot>\Admin, <WebRoot>\ETANotifications

    All apps share ONE app pool named after -BasePath (default "EInvoiceAPI"): No Managed Code, AlwaysRunning,
    32-bit when -Arch x86 (needed by the USB token driver eps2003csp11.dll), identity ApplicationPoolIdentity.
    The apps are published with the out-of-process hosting model (each runs in its own process behind the
    ASP.NET Core Module), which is what allows several ASP.NET Core apps in one pool.
    The pool gets Database__Path, Database__Password and ASPNETCORE_ENVIRONMENT=Production, and Modify rights on
    the config.db folder. Optionally adds an HTTPS binding with an existing certificate.

    App pools that the apps used before (for example the per-app pools of earlier versions, or the pools of an
    older manual deployment) are deleted once no application uses them any more.

    -NotificationsSiteName additionally serves ETANotifications at the ROOT of another site (for example a site
    with the public https binding that ETA already calls), from the same folder and the same app pool, so the
    URLs registered with ETA (https://<host>/api/einvoice/...) keep working and Admin is not exposed there.

    Safe to re-run: existing pools, applications and bindings are updated, not duplicated.
    Prerequisites: IIS and the ASP.NET Core 10.0 Hosting Bundle.

.PARAMETER DatabasePassword
    config.db password, the same for all apps, at least 32 characters. If omitted, the ETA_DB_PASSWORD
    environment variable is used (this is how EInvoiceAPI-Setup.exe passes it), otherwise you are prompted.

.PARAMETER StopAppPools
    Only stop the app pools (and wait until they have stopped) so their files can be replaced; then exit.

.PARAMETER StartAppPools
    Only start the app pools that exist; then exit (used to restore them when setup stops early).

.PARAMETER SetPoolPassword
    Only set Database__Password (from -DatabasePassword / ETA_DB_PASSWORD) on the app pools that exist, then start
    them; then exit. Used after the database password was changed, so every app (installed now or earlier) gets it.

.PARAMETER NotificationsSiteName
    Optional existing IIS site whose root application is pointed at ETANotifications (folder and app pool).

.PARAMETER Remove
    Remove the selected IIS applications, and the app pool once no application uses it; then exit. Files,
    config.db, the notification logs, any HTTPS binding and the -NotificationsSiteName site are left in place.
    Used by the uninstaller.

.EXAMPLE
    .\Setup-IIS.ps1                                          # Default Web Site, x64, all three apps
.EXAMPLE
    .\Setup-IIS.ps1 -Arch x86 -SiteName "ETA" -BasePath EInvoiceAPI -CertificateThumbprint 3A1F...C9
.EXAMPLE
    .\Setup-IIS.ps1 -Apps EInvoiceAPI,Admin                  # without ETANotifications
.EXAMPLE
    .\Setup-IIS.ps1 -NotificationsSiteName ETANotif          # ETA keeps calling https://<ETANotif host>/api/einvoice/...
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $SiteName = 'Default Web Site',

    # First URL segment; the apps live at /<BasePath>, /<BasePath>/Admin and /<BasePath>/ETANotifications.
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string] $BasePath = 'EInvoiceAPI',

    # Parent folder for the app folders.
    [string] $WebRoot = 'C:\inetpub\EInvoiceAPI',

    # Must match the publish profile used (FolderProfile-x64 / FolderProfile-x86).
    [ValidateSet('x64', 'x86')]
    [string] $Arch = 'x64',

    # Any of EInvoiceAPI, Admin, ETANotifications; also accepts one comma-separated string.
    [string[]] $Apps = @('EInvoiceAPI', 'Admin', 'ETANotifications'),

    # Shared LiteDB file; keep it outside the web folders.
    [string] $DatabasePath = 'C:\ETA\Data\config.db',

    # Folder ETANotifications (IncomeTaxWebAPI) writes its SQLite notification logs to.
    [string] $NotificationsDataFolder = 'C:\EInvoice',

    [securestring] $DatabasePassword,

    # Thumbprint of a certificate in LocalMachine\My. When set, an https binding is added/updated on the site.
    [string] $CertificateThumbprint,
    [int] $HttpsPort = 443,
    # Optional host name for the https binding (uses SNI when set).
    [string] $HttpsHostName = '',

    # Optional: also serve ETANotifications at the root of this existing site (same folder and app pool).
    [string] $NotificationsSiteName = '',

    [switch] $StopAppPools,

    [switch] $StartAppPools,

    [switch] $SetPoolPassword,

    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

# EInvoiceAPI-Setup.exe sets ETA_SETUP_TRANSCRIPT to collect this script's output for its log.
if ($env:ETA_SETUP_TRANSCRIPT) { Start-Transcript -Path $env:ETA_SETUP_TRANSCRIPT -Append | Out-Null }

$mwa = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
if (-not (Test-Path $mwa)) { throw 'IIS is not installed (Microsoft.Web.Administration.dll not found).' }
Add-Type -Path $mwa

# One shared app pool for every app (out-of-process hosting).
$PoolName = $BasePath
# Per-app pools created by earlier versions of this script; deleted once unused.
$LegacyPools = @("$BasePath-Admin", "$BasePath-ETANotifications")
$definitions = [ordered]@{
    EInvoiceAPI      = @{ Pool = $PoolName; Url = "/$BasePath" }
    Admin            = @{ Pool = $PoolName; Url = "/$BasePath/Admin" }
    ETANotifications = @{ Pool = $PoolName; Url = "/$BasePath/ETANotifications" }
}
$Apps = @($Apps | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($a in $Apps) { if (-not $definitions.Contains($a)) { throw "Unknown app '$a'. Use EInvoiceAPI, Admin or ETANotifications." } }
$selected = @($definitions.Keys | Where-Object { $Apps -contains $_ })

# Pools the selected apps use (now one), plus earlier per-app pools that may still exist.
$poolNames = @($PoolName) + $LegacyPools

function Test-PoolInUse($sm, [string] $name) {
    foreach ($s in $sm.Sites) { foreach ($a in $s.Applications) { if ($a.ApplicationPoolName -eq $name) { return $true } } }
    return $false
}

# Deletes the given pools when no application on any site uses them.
function Remove-UnusedPools($sm, [string[]] $names) {
    $removed = $false
    foreach ($name in @($names | Where-Object { $_ } | Select-Object -Unique)) {
        # Never delete the pools IIS and the .NET Framework create.
        if ($name -eq 'DefaultAppPool' -or $name -like '*.NET*') { continue }
        $pool = $sm.ApplicationPools[$name]
        if ($null -eq $pool) { continue }
        if (Test-PoolInUse $sm $name) { Write-Host "App pool $name is still used by other applications; kept."; continue }
        try { [void]$pool.Stop() } catch { }
        $sm.ApplicationPools.Remove($pool)
        Write-Host "Removed unused app pool $name"
        $removed = $true
    }
    return $removed
}

# --- Remove mode -----------------------------------------------------------------------------------------
if ($Remove) {
    $sm = [Microsoft.Web.Administration.ServerManager]::new()
    $site = $sm.Sites[$SiteName]
    # Children first, then the base application.
    foreach ($name in @($selected | Sort-Object { if ($_ -eq 'EInvoiceAPI') { 1 } else { 0 } })) {
        $def = $definitions[$name]
        if ($null -ne $site) {
            $app = $site.Applications[$def.Url]
            if ($null -ne $app) { $site.Applications.Remove($app); Write-Host "Removed application $($def.Url)" }
        }
    }
    [void](Remove-UnusedPools $sm $poolNames)
    $sm.CommitChanges()
    Start-Sleep -Seconds 3   # let worker processes exit before files are deleted
    return
}

# --- Stop mode -------------------------------------------------------------------------------------------
if ($StopAppPools) {
    $sm = [Microsoft.Web.Administration.ServerManager]::new()
    foreach ($name in $poolNames) {
        $pool = $sm.ApplicationPools[$name]
        if ($null -eq $pool) { continue }
        if ($pool.State -ne [Microsoft.Web.Administration.ObjectState]::Stopped) {
            Write-Host "Stopping app pool $($pool.Name)..."
            try { [void]$pool.Stop() } catch { Write-Warning $_.Exception.Message }
        }
        for ($i = 0; $i -lt 60 -and $pool.State -ne [Microsoft.Web.Administration.ObjectState]::Stopped; $i++) {
            Start-Sleep -Milliseconds 500
        }
    }
    # Give worker processes a moment to release file locks.
    Start-Sleep -Seconds 2
    return
}

# --- Start mode / set-password mode ----------------------------------------------------------------------
if ($StartAppPools -or $SetPoolPassword) {
    $sm = [Microsoft.Web.Administration.ServerManager]::new()
    if ($SetPoolPassword) {
        $newPassword = if ($DatabasePassword) { [System.Net.NetworkCredential]::new('', $DatabasePassword).Password } else { $env:ETA_DB_PASSWORD }
        if ([string]::IsNullOrEmpty($newPassword) -or $newPassword.Length -lt 32) { throw 'The database password must be at least 32 characters.' }
        $pools = $sm.GetApplicationHostConfiguration().GetSection('system.applicationHost/applicationPools').GetCollection()
        foreach ($name in $poolNames) {
            $element = $pools | Where-Object { $_['name'] -eq $name } | Select-Object -First 1
            if ($null -eq $element) { continue }
            $variables = $element.GetCollection('environmentVariables')
            foreach ($existing in @($variables | Where-Object { $_['name'] -eq 'Database__Password' })) { $variables.Remove($existing) }
            $entry = $variables.CreateElement('add')
            $entry['name'] = 'Database__Password'
            $entry['value'] = $newPassword
            [void]$variables.Add($entry)
            Write-Host "Set the database password on app pool $name"
        }
        $sm.CommitChanges()
        $newPassword = $null
        $sm = [Microsoft.Web.Administration.ServerManager]::new()
    }
    foreach ($name in $poolNames) {
        $pool = $sm.ApplicationPools[$name]
        if ($null -eq $pool -or $pool.State -eq [Microsoft.Web.Administration.ObjectState]::Started) { continue }
        try { [void]$pool.Start(); Write-Host "Started app pool $($pool.Name)" } catch { Write-Warning "Could not start $($pool.Name): $($_.Exception.Message)" }
    }
    return
}

# --- Configure mode --------------------------------------------------------------------------------------
$plainPassword = $null
if ($DatabasePassword) { $plainPassword = [System.Net.NetworkCredential]::new('', $DatabasePassword).Password }
elseif ($env:ETA_DB_PASSWORD) { $plainPassword = $env:ETA_DB_PASSWORD }
else {
    $secure = Read-Host -AsSecureString 'Database password for config.db (same for all apps, 32+ characters)'
    $plainPassword = [System.Net.NetworkCredential]::new('', $secure).Password
}
if ($plainPassword.Length -lt 32) { throw 'The database password must be at least 32 characters.' }

$sm = [Microsoft.Web.Administration.ServerManager]::new()
$site = $sm.Sites[$SiteName]
if ($null -eq $site) { throw "IIS site '$SiteName' not found." }
$notificationsSite = $null
if ($NotificationsSiteName) {
    if ($selected -notcontains 'ETANotifications') { throw '-NotificationsSiteName needs the ETANotifications app.' }
    if ($NotificationsSiteName -eq $SiteName) { throw '-NotificationsSiteName must be a different site than -SiteName.' }
    $notificationsSite = $sm.Sites[$NotificationsSiteName]
    if ($null -eq $notificationsSite) { throw "IIS site '$NotificationsSiteName' not found." }
}

$pool = $sm.ApplicationPools[$PoolName]
if ($null -eq $pool) { $pool = $sm.ApplicationPools.Add($PoolName) }
$pool.ManagedRuntimeVersion = ''                                   # No Managed Code
$pool.Enable32BitAppOnWin64 = ($Arch -eq 'x86')
$pool.StartMode = [Microsoft.Web.Administration.StartMode]::AlwaysRunning
$pool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity

# Pools the apps used before this run (earlier versions or a manual deployment); deleted below once unused.
$previousPools = [System.Collections.Generic.List[string]]::new()
$previousPools.AddRange([string[]]$LegacyPools)

foreach ($name in $selected) {
    $def = $definitions[$name]
    $physicalPath = Join-Path $WebRoot $name
    New-Item -ItemType Directory -Force -Path $physicalPath | Out-Null

    $app = $site.Applications[$def.Url]
    if ($null -eq $app) { $app = $site.Applications.Add($def.Url, $physicalPath) }
    else { $previousPools.Add($app.ApplicationPoolName); $app.VirtualDirectories['/'].PhysicalPath = $physicalPath }
    $app.ApplicationPoolName = $PoolName
}

if ($null -ne $notificationsSite) {
    $root = $notificationsSite.Applications['/']
    $previousPools.Add($root.ApplicationPoolName)
    $root.VirtualDirectories['/'].PhysicalPath = Join-Path $WebRoot 'ETANotifications'
    $root.ApplicationPoolName = $PoolName
    Write-Host ("Site '{0}' root -> {1} (pool {2})" -f $NotificationsSiteName, (Join-Path $WebRoot 'ETANotifications'), $PoolName) -ForegroundColor Green
}

if ($CertificateThumbprint) {
    $thumb = ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = Get-Item "Cert:\LocalMachine\My\$thumb" -ErrorAction SilentlyContinue
    if ($null -eq $cert) { throw "Certificate $thumb not found in LocalMachine\My." }
    $info = "*:$($HttpsPort):$HttpsHostName"
    foreach ($b in @($site.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq $info })) {
        $site.Bindings.Remove($b)
    }
    $binding = $site.Bindings.Add($info, $cert.GetCertHash(), 'My')
    if ($HttpsHostName) { $binding.SslFlags = [Microsoft.Web.Administration.SslFlags]::Sni }
    Write-Host "HTTPS binding $info -> certificate $thumb ($($cert.Subject))" -ForegroundColor Green
}

$sm.CommitChanges()

$sm = [Microsoft.Web.Administration.ServerManager]::new()
if (Remove-UnusedPools $sm @($previousPools | Where-Object { $_ -and $_ -ne $PoolName })) { $sm.CommitChanges() }

# Environment variables on the pool (applicationHost.config, encrypted at rest by IIS only if configured;
# the file is readable by Administrators and the IIS system accounts).
$sm = [Microsoft.Web.Administration.ServerManager]::new()
$poolsSection = $sm.GetApplicationHostConfiguration().GetSection('system.applicationHost/applicationPools').GetCollection()
function Set-PoolEnvironmentVariable($poolName, [string] $name, [string] $value) {
    $poolElement = $poolsSection | Where-Object { $_['name'] -eq $poolName } | Select-Object -First 1
    $variables = $poolElement.GetCollection('environmentVariables')
    foreach ($existing in @($variables | Where-Object { $_['name'] -eq $name })) { $variables.Remove($existing) }
    $entry = $variables.CreateElement('add')
    $entry['name'] = $name
    $entry['value'] = $value
    [void]$variables.Add($entry)
}
Set-PoolEnvironmentVariable $PoolName 'ASPNETCORE_ENVIRONMENT' 'Production'
Set-PoolEnvironmentVariable $PoolName 'Database__Path' $DatabasePath
Set-PoolEnvironmentVariable $PoolName 'Database__Password' $plainPassword
if ($selected -contains 'ETANotifications') { Set-PoolEnvironmentVariable $PoolName 'Notifications__DataFolder' $NotificationsDataFolder }
$sm.CommitChanges()
$plainPassword = $null

# Folder permissions for the pool identities ("IIS AppPool\<pool>").
function Grant-Access([string] $path, [string] $pool, [string] $rights) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    & icacls $path /grant "IIS AppPool\$($pool):(OI)(CI)$rights" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $path" }
}
$dataFolder = Split-Path -Parent $DatabasePath
foreach ($name in $selected) { Grant-Access (Join-Path $WebRoot $name) $PoolName 'RX' }
Grant-Access $dataFolder $PoolName 'M'
if ($selected -contains 'ETANotifications') { Grant-Access $NotificationsDataFolder $PoolName 'M' }

# Start the pools (they may have been stopped for an upgrade).
$sm = [Microsoft.Web.Administration.ServerManager]::new()
$pool = $sm.ApplicationPools[$PoolName]
if ($pool.State -ne [Microsoft.Web.Administration.ObjectState]::Started) {
    try { [void]$pool.Start() } catch { Write-Warning "Could not start $($pool.Name): $($_.Exception.Message)" }
}
foreach ($name in $selected) {
    Write-Host ("{0,-34} -> {1}  (pool {2}, {3})" -f $definitions[$name].Url, (Join-Path $WebRoot $name), $pool.Name, $Arch) -ForegroundColor Green
}
