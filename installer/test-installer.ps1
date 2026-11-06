<#
.SYNOPSIS
    End-to-end test of EInvoiceAPI-Setup.exe on a disposable Windows Server (used by CI).
    Do not run on a real server: it installs IIS, changes Default Web Site and uninstalls the apps.

.DESCRIPTION
    1. Fresh silent install (also installs IIS and the Hosting Bundle if missing), x64, HTTPS binding with a
       self-signed certificate, SAP Prod pointing at a local fake SAP. Checks all three URLs, Admin login,
       ETA notification forwarding to SAP with the configured credentials, and the SQLite log.
    2. Upgrade in place to x86 without passing the database password (setup must reuse it from IIS).
    3. Uninstall: the IIS applications disappear, config.db is kept.
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
$apiKey = @{ Authorization = 'ApiKey FreshTax2022' }

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
        "/CERT=$($cert.Thumbprint)")
    Check ($code -eq 0) 'fresh install exits with 0'

    Check ((Invoke-Iis "(Get-ItemProperty 'IIS:\AppPools\EInvoiceAPI' -Name enable32BitAppOnWin64).Value") -eq 'False') 'x64 install: 64-bit app pool'
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
    }
    Invoke-Phase 'fresh install: Admin' { Test-AdminLogin 'fresh install:' }

    $r = Send-Http Get 'http://localhost/EInvoiceAPI/Admin'
    Check ($r.StatusCode -in 301, 302, 307, 308 -and $r.Location -like 'https://*') "Admin redirects http to https ($($r.StatusCode))"

    # === 2. upgrade to x86, password reused from IIS ======================================================
    $code = Invoke-Setup 'upgrade' @('/ARCH=x86')
    Check ($code -eq 0) 'upgrade (x86, no password given) exits with 0'
    Check ((Invoke-Iis "(Get-ItemProperty 'IIS:\AppPools\EInvoiceAPI' -Name enable32BitAppOnWin64).Value") -eq 'True') 'x86 upgrade: 32-bit app pool'
    $r = Send-Http Put "$notifications/ping" $apiKey '{"Rin":"456"}'
    Check ($r.StatusCode -eq 200) "ETANotifications works after upgrade ($($r.StatusCode))"
    $r = Send-Http Get "$api/swagger/v1/swagger.json"
    Check ($r.StatusCode -eq 200) "EInvoiceAPI works after upgrade ($($r.StatusCode))"
    Invoke-Phase 'after upgrade: Admin' { Test-AdminLogin 'after upgrade:' }

    # === 3. uninstall =====================================================================================
    $uninstaller = 'C:\inetpub\EInvoiceAPI\unins000.exe'
    $p = Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $LogDir 'uninstall.log')`"" -Wait -PassThru
    Check ($p.ExitCode -eq 0) 'uninstall exits with 0'
    Start-Sleep -Seconds 5
    Check ((Invoke-Iis "@(Get-WebApplication -Site 'Default Web Site' -Name 'EInvoiceAPI').Count") -eq '0') 'uninstall removed the IIS applications'
    Check ((Invoke-Iis "Test-Path 'IIS:\AppPools\EInvoiceAPI-Admin'") -eq 'False') 'uninstall removed the app pools'
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
