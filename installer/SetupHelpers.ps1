<#
    Helper actions for EInvoiceAPI-Setup.exe (Inno Setup). Each action writes its result to -Out
    (UTF-8, one item per line) so the wizard can read it. Not meant to be run by hand.

    ListSites         IIS site names
    ListCertificates  "thumbprint|subject|expiry" for LocalMachine\My certificates with a private key
    GetPoolPassword   Database__Password of app pool -Pool (to reuse on upgrade)
    NewPassword       a random 32-byte password (Base64)
    InstallIIS        installs IIS (Windows Server feature or Windows client optional feature)
    ExpandZip         extracts -Zip into -Destination
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('ListSites', 'ListCertificates', 'GetPoolPassword', 'NewPassword', 'InstallIIS', 'ExpandZip')]
    [string] $Action,
    [string] $Out,
    [string] $Pool,
    [string] $Zip,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'

# Errors go to <Out>.err so the setup log can show them (never write passwords in error messages).
trap {
    if ($Out) { ($_ | Out-String) + $_.ScriptStackTrace | Set-Content -Path "$Out.err" -Encoding UTF8 }
    exit 1
}

function Write-Result([string[]] $lines) {
    if ($Out) { Set-Content -Path $Out -Value $lines -Encoding UTF8 }
}

function Get-ServerManager {
    $mwa = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
    if (-not (Test-Path $mwa)) { return $null }
    Add-Type -Path $mwa
    return [Microsoft.Web.Administration.ServerManager]::new()
}

switch ($Action) {
    'ListSites' {
        $sm = Get-ServerManager
        if ($null -eq $sm) { Write-Result @(); break }
        Write-Result @($sm.Sites | ForEach-Object { $_.Name })
    }

    'ListCertificates' {
        $now = Get-Date
        Write-Result @(Get-ChildItem Cert:\LocalMachine\My |
            Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt $now } |
            Sort-Object NotAfter -Descending |
            ForEach-Object {
                $name = if ($_.FriendlyName) { $_.FriendlyName } else { $_.Subject }
                '{0}|{1}|{2:yyyy-MM-dd}' -f $_.Thumbprint, ($name -replace '\|', '/'), $_.NotAfter
            })
    }

    'GetPoolPassword' {
        $sm = Get-ServerManager
        $value = ''
        if ($null -ne $sm -and $Pool) {
            $pools = $sm.GetApplicationHostConfiguration().GetSection('system.applicationHost/applicationPools').GetCollection()
            $element = $pools | Where-Object { $_['name'] -eq $Pool } | Select-Object -First 1
            if ($null -ne $element) {
                $variable = $element.GetCollection('environmentVariables') | Where-Object { $_['name'] -eq 'Database__Password' } | Select-Object -First 1
                if ($null -ne $variable) { $value = [string]$variable['value'] }
            }
        }
        Write-Result @($value)
    }

    'NewPassword' {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($bytes)
        $rng.Dispose()
        Write-Result @([Convert]::ToBase64String($bytes))
    }

    'InstallIIS' {
        if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
            $result = Install-WindowsFeature -Name Web-Server, Web-Mgmt-Console
            if (-not $result.Success) { throw 'Install-WindowsFeature Web-Server failed.' }
        }
        else {
            Enable-WindowsOptionalFeature -Online -All -NoRestart `
                -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-ManagementConsole | Out-Null
        }
        Write-Result @('OK')
    }

    'ExpandZip' {
        Expand-Archive -LiteralPath $Zip -DestinationPath $Destination -Force
        Write-Result @('OK')
    }
}
