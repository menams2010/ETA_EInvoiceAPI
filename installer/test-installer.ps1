<#
.SYNOPSIS
    End-to-end test of EInvoiceAPI-Setup.exe on a disposable Windows Server (used by CI).
    Do not run on a real server: it installs IIS, changes Default Web Site and uninstalls the apps.

.DESCRIPTION
    1. Fresh silent install (also installs IIS and the Hosting Bundle if missing), x64, HTTPS binding with a
       self-signed certificate, SAP Prod pointing at a local fake SAP. Checks all three URLs, Admin login,
       ETA notification forwarding to SAP with the configured credentials, and the SQLite log.
       All apps share the one app pool "EInvoiceAPI".
    2. Upgrade in place to x86 without passing the database password (setup must reuse it from IIS), changing
       the password, with an older layout simulated first (Admin on its own LocalSystem pool, a separate
       "ETANotif" site with its own pool): /NOTIFSITE=ETANotif points that site's root at ETANotifications, and
       the old pools are deleted.
    3. Uninstall: the IIS applications and the app pool disappear, config.db is kept.
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [string] $LogDir = (Join-Path (Get-Location) 'test-logs')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "PASS  $what" -ForegroundColor Green } else { Write-Host "FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
# Runs a group of checks; an unexpected error fails that group but the remaining phases still run.
function Invoke-Phase([string] $name, [scriptblock] $block) {
    try { & $block }
    catch { Check $false "$name threw: $($_.Exception.Message -replace '\s+', ' ' | ForEach-Object { $_.Substring(0, [Math]::Min(300, $_.Length)) })" }
}
function Invoke-Setup([string] $name, [string[]] $arguments) {
    $log = Join-Path $LogDir "$name.log"
    $p = Start-Process -FilePath $Setup -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $arguments) -Wait -PassThru
    Write-Host "$name exit code: $($p.ExitCode)"
    return $p.ExitCode
}
# The WebAdministration module (IIS: drive) only works in Windows PowerShell, so IIS queries go through powershell.exe.
function Invoke-Iis([string] $command) {
    $env:PSModulePath = "$env:ProgramFiles\WindowsPowerShell\Modules;$env:windir\System32\WindowsPowerShell\v1.0\Modules"
    $output = & "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "Import-Module WebAdministration; $command"
    return "$output".Trim()
}
# HTTP via HttpClient: no automatic redirects (so 3xx can be checked), a cookie jar per client, test certificate accepted.
function New-Client {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    return $client
}
$http = New-Client
# Returns StatusCode, Location and Content. $Body: a string (sent as JSON) or a hashtable (sent as a form).
function Send-Http([string] $Method, [string] $Url, [hashtable] $Headers = @{}, $Body = $null, $Client = $http) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method.ToUpperInvariant()), $Url)
    foreach ($key in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($key, [string]$Headers[$key]) }
    if ($Body -is [hashtable]) {
        $fields = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
        foreach ($key in $Body.Keys) { $fields.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($key, [string]$Body[$key])) }
        $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($fields)
    }
    elseif ($null -ne $Body) {
        $request.Content = [System.Net.Http.StringContent]::new([string]$Body, [Text.Encoding]::UTF8, 'application/json')
    }
    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Location   = "$($response.Headers.Location)"
        Content    = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

$adminUser = 'ciadmin'
$adminPassword = 'CiAdminPassw0rd!'
$api = 'https://localhost/EInvoiceAPI'
$admin = 'https://localhost/EInvoiceAPI/Admin'
$notifications = 'https://localhost/EInvoiceAPI/ETANotifications/api/einvoice'
$installKey = 'CiInstallKey-123'
$changedKey = 'CiChangedKey-456'
$apiKey = @{ Authorization = "ApiKey $installKey" }

# --- fake SAP: records what ETANotifications forwards -----------------------------------------------------
$sapLog = Join-Path $LogDir 'fake-sap.jsonl'
$fakeSap = Join-Path $LogDir 'fake-sap.ps1'
@"
`$listener = [System.Net.HttpListener]::new()
`$listener.Prefixes.Add('http://localhost:7099/')
`$listener.Start()
while (`$true) {
    `$ctx = `$listener.GetContext()
    `$body = [System.IO.StreamReader]::new(`$ctx.Request.InputStream).ReadToEnd()
    [pscustomobject]@{ path = `$ctx.Request.RawUrl; auth = `$ctx.Request.Headers['Authorization']; body = `$body } |
        ConvertTo-Json -Compress | Add-Content -Path '$sapLog'
    `$ctx.Response.StatusCode = 200
    `$ctx.Response.Close()
}
"@ | Set-Content $fakeSap
$sapProcess = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $fakeSap -PassThru -WindowStyle Hidden

# --- test certificate for the HTTPS binding ---------------------------------------------------------------
$cert = New-SelfSignedCertificate -DnsName localhost -CertStoreLocation Cert:\LocalMachine\My -FriendlyName 'EInvoiceAPI CI'

try {
    # === 1. fresh install ================================================================================
    $code = Invoke-Setup 'install' @(
        '/INSTALLIIS=yes', '/ARCH=x64', "/ADMINUSER=$adminUser", "/ADMINPASSWORD=$adminPassword",
        '/SAPPRODURL=http://localhost:7099/zeinvoice?sap-client=300', '/SAPPRODUSER=ci.sap', '/SAPPRODPASSWORD=ci-sap-secret',
        "/NOTIFAPIKEY=$installKey",
        "/CERT=$($cert.Thumbprint)")
    Check ($code -eq 0) 'fresh install exits with 0'

    Check ((Invoke-Iis "(Get-ItemProperty 'IIS:\AppPools\EInvoiceAPI' -Name enable32BitAppOnWin64).Value") -eq 'False') 'x64 install: 64-bit app pool'
    $appPools = Invoke-Iis "(Get-WebApplication -Site 'Default Web Site' | Where-Object { `$_.path -like '/EInvoiceAPI*' } | ForEach-Object { `$_.applicationPool } | Sort-Object -Unique) -join ','"
    Check ($appPools -eq 'EInvoiceAPI') "all three apps share the app pool EInvoiceAPI ($appPools)"
    Check ((Invoke-Iis "@(Get-ChildItem IIS:\AppPools | Where-Object { `$_.Name -like 'EInvoiceAPI*' }).Count") -eq '1') 'only one EInvoiceAPI app pool exists'
    Check ((Invoke-Iis "@(Get-WebBinding -Name 'Default Web Site' -Protocol https -Port 443).Count") -eq '1') 'HTTPS binding added'
    Check (Test-Path 'C:\ETA\Data\config.db') 'config.db created'

    $r = Send-Http Get "$api/swagger/v1/swagger.json"
    Check ($r.StatusCode -eq 200 -and $r.Content -match '"/EInvoiceAPI"') "EInvoiceAPI Swagger at /EInvoiceAPI ($($r.StatusCode))"

    $r = Send-Http Put "$notifications/ping" $apiKey '{"Rin":"123"}'
    Check ($r.StatusCode -eq 200 -and $r.Content -match '123') "ETANotifications ping ($($r.StatusCode))"
    $r = Send-Http Put "$notifications/ping" @{} '{}'
    Check ($r.StatusCode -eq 401) "ETANotifications rejects a missing API key ($($r.StatusCode))"

    $delivery = '{"DeliveryId":"CI-1","Type":"DocumentReceived","Count":1,"Message":[{"Type":"DocumentReceived","UUId":"U1","SubmissionUUId":"S1","LongId":"L1","InternalId":"I1","Status":"Valid"}]}'
    $r = Send-Http Put "$notifications/notifications/documents" $apiKey $delivery
    Check ($r.StatusCode -eq 200) "notification forwarded to SAP ($($r.StatusCode))"
    $sent = if (Test-Path $sapLog) { Get-Content $sapLog | Select-Object -Last 1 | ConvertFrom-Json } else { $null }
    $expectedAuth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('ci.sap:ci-sap-secret'))
    Check ($null -ne $sent -and $sent.auth -eq $expectedAuth -and $sent.body -match 'CI-1') 'fake SAP received the notification with the credentials entered in setup'
    Check (Test-Path 'C:\EInvoice\ProdDB.db') 'notification saved to C:\EInvoice\ProdDB.db'

    function Test-AdminLogin([string] $label) {
        $session = New-Client
        $page = Send-Http Get "$admin/Home/Login" -Client $session
        Check ($page.StatusCode -eq 200) "$label Admin login page ($($page.StatusCode))"
        $token = [regex]::Match($page.Content, 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"').Groups[1].Value
        $login = Send-Http Post $admin -Client $session -Body @{ u_name = $adminUser; u_pass = $adminPassword; __RequestVerificationToken = $token }
        Check ($login.StatusCode -eq 302 -and $login.Location -match '/EInvoiceAPI/Admin/Home/Index') "$label Admin login ($($login.StatusCode) -> $($login.Location))"
        $sap = Send-Http Get "$admin/SapEndpoints/Index" -Client $session
        Check ($sap.StatusCode -eq 200 -and $sap.Content -match 'localhost:7099' -and $sap.Content -match 'ci\.sap') "$label SAP Endpoints page shows the setup values"
        return $session
    }
    Invoke-Phase 'fresh install: Admin' {
        $session = Test-AdminLogin 'fresh install:'
        $page = Send-Http Get "$admin/SapEndpoints/Index" -Client $session
        Check ($page.Content -match [regex]::Escape($installKey)) 'Admin shows the API key entered in setup'

        # Change the API key in Admin: the old key must stop working and the new one work, without a restart.
        $form = Send-Http Get "$admin/SapEndpoints/ApiKey" -Client $session
        $token = [regex]::Match($form.Content, 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"').Groups[1].Value
        $save = Send-Http Post "$admin/SapEndpoints/ApiKey" -Client $session -Body @{
            mode = 'custom'; apiKey = $changedKey; confirmApiKey = $changedKey; __RequestVerificationToken = $token }
        Check ($save.StatusCode -eq 302) "Admin saves a new API key ($($save.StatusCode))"
        $r = Send-Http Put "$notifications/ping" $apiKey '{"Rin":"1"}'
        Check ($r.StatusCode -eq 401) "old API key rejected after the change ($($r.StatusCode))"
        $script:apiKey = @{ Authorization = "ApiKey $changedKey" }
        $r = Send-Http Put "$notifications/ping" $script:apiKey '{"Rin":"2"}'
        Check ($r.StatusCode -eq 200) "new API key accepted immediately ($($r.StatusCode))"
    }

    $r = Send-Http Get 'http://localhost/EInvoiceAPI/Admin'
    Check ($r.StatusCode -in 301, 302, 307, 308 -and $r.Location -like 'https://*') "Admin redirects http to https ($($r.StatusCode))"

    # === 2. upgrade to x86, password reused from IIS ======================================================
    # Simulate the older manual deployment: Admin on its own LocalSystem pool, ETA notifications on a separate
    # site with its own pool (port 8081 here instead of the real public https binding).
    New-Item -ItemType Directory -Force -Path C:\OldETANotif | Out-Null
    Set-Content C:\OldETANotif\index.html 'old notifications app'
    Invoke-Iis ("New-WebAppPool EInvoiceAPI_Admin | Out-Null; Set-ItemProperty IIS:\AppPools\EInvoiceAPI_Admin -Name processModel.identityType -Value LocalSystem; " +
        "Set-ItemProperty 'IIS:\Sites\Default Web Site\EInvoiceAPI\Admin' -Name applicationPool -Value EInvoiceAPI_Admin; " +
        "New-WebAppPool ETANotif | Out-Null; New-Website -Name ETANotif -Port 8081 -PhysicalPath C:\OldETANotif -ApplicationPool ETANotif | Out-Null") | Out-Null
    Check ((Invoke-Iis "(Get-Item 'IIS:\Sites\Default Web Site\EInvoiceAPI\Admin').applicationPool") -eq 'EInvoiceAPI_Admin') 'older layout simulated'
    # Also changes the database password to one chosen here (the current one is read from IIS).
    $newDbPassword = 'Ci-chosen-database-password-0123456789'
    $code = Invoke-Setup 'upgrade' @('/ARCH=x86', "/NEWDBPASSWORD=$newDbPassword", '/NOTIFSITE=ETANotif')
    Check ($code -eq 0) 'upgrade (x86, current password from IIS, new password chosen) exits with 0'
    Check ((Invoke-Iis "(Get-Item 'IIS:\Sites\Default Web Site\EInvoiceAPI\Admin').applicationPool") -eq 'EInvoiceAPI') 'upgrade moved Admin to the EInvoiceAPI app pool'
    Check ((Invoke-Iis "(Get-Item 'IIS:\Sites\ETANotif').applicationPool") -eq 'EInvoiceAPI') 'ETANotif site root uses the EInvoiceAPI app pool'
    Check ((Invoke-Iis "(Get-Item 'IIS:\Sites\ETANotif').physicalPath") -eq 'C:\inetpub\EInvoiceAPI\ETANotifications') 'ETANotif site root points at ETANotifications'
    Check ((Invoke-Iis "(Test-Path IIS:\AppPools\EInvoiceAPI_Admin) -or (Test-Path IIS:\AppPools\ETANotif)") -eq 'False') 'old app pools deleted'
    foreach ($pool in @('EInvoiceAPI')) {
        $value = Invoke-Iis ("Add-Type -Path `"`$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll`"; " +
            "`$pools = [Microsoft.Web.Administration.ServerManager]::new().GetApplicationHostConfiguration().GetSection('system.applicationHost/applicationPools').GetCollection(); " +
            "((`$pools | Where-Object { `$_['name'] -eq '$pool' }).GetCollection('environmentVariables') | Where-Object { `$_['name'] -eq 'Database__Password' })['value']")
        Check ($value -eq $newDbPassword) "app pool $pool has the new database password"
    }
    Check (@(Get-ChildItem 'C:\ETA\Data' -Filter 'config.before-password-change-*.db').Count -eq 1) 'backup of the database with the old password kept'
    Check ((Invoke-Iis "(Get-ItemProperty 'IIS:\AppPools\EInvoiceAPI' -Name enable32BitAppOnWin64).Value") -eq 'True') 'x86 upgrade: 32-bit app pool'
    $r = Send-Http Put "$notifications/ping" $apiKey '{"Rin":"456"}'
    Check ($r.StatusCode -eq 200) "ETANotifications works after upgrade, API key kept ($($r.StatusCode))"
    $r = Send-Http Get "$api/swagger/v1/swagger.json"
    Check ($r.StatusCode -eq 200) "EInvoiceAPI works after upgrade ($($r.StatusCode))"
    Invoke-Phase 'after upgrade: Admin' { $null = Test-AdminLogin 'after upgrade:' }
    $r = Send-Http Put 'http://localhost:8081/api/einvoice/ping' $apiKey '{"Rin":"789"}'
    Check ($r.StatusCode -eq 200 -and $r.Content -match '789') "ETANotifications at the root of the ETANotif site ($($r.StatusCode))"
    $r = Send-Http Get 'http://localhost:8081/EInvoiceAPI/Admin'
    Check ($r.StatusCode -in 401, 404) "Admin is not published on the ETANotif site ($($r.StatusCode))"

    # The ETANotif site keeps using the pool after uninstall; remove it first so the pool can go too.
    Invoke-Iis "Remove-Website ETANotif" | Out-Null

    # === 3. uninstall =====================================================================================
    $uninstaller = 'C:\inetpub\EInvoiceAPI\unins000.exe'
    $p = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $LogDir 'uninstall.log')`"" -Wait -PassThru
    Check ($p.ExitCode -eq 0) 'uninstall exits with 0'
    Start-Sleep -Seconds 5
    Check ((Invoke-Iis "@(Get-WebApplication -Site 'Default Web Site' -Name 'EInvoiceAPI').Count") -eq '0') 'uninstall removed the IIS applications'
    Check ((Invoke-Iis "Test-Path 'IIS:\AppPools\EInvoiceAPI'") -eq 'False') 'uninstall removed the app pool'
    Check (Test-Path 'C:\ETA\Data\config.db') 'uninstall kept config.db'
}
finally {
    Stop-Process -Id $sapProcess.Id -ErrorAction SilentlyContinue
    Copy-Item C:\inetpub\EInvoiceAPI\*.log $LogDir -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nAll installer checks passed." -ForegroundColor Green
