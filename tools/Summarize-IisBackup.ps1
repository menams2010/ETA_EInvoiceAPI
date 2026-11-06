# Summarizes an IIS configuration backup folder without printing secret values.
# Usage: powershell -ExecutionPolicy Bypass -File Summarize-IisBackup.ps1 -Root C:\Users\Mina\Desktop\srviis1-backup-2019 > iis-summary.txt
param([string]$Root = 'C:\Users\Mina\Desktop\srviis1-backup-2019')
$secret = '(?i)(password|pwd|secret|key|token|connectionString|validationKey|decryptionKey)'

"=== Config files"
Get-ChildItem $Root -Recurse -File -Include applicationHost.config, administration.config, redirection.config, web.config, *.config.bak -ErrorAction SilentlyContinue |
  ForEach-Object { "{0,10:N0}  {1}" -f $_.Length, $_.FullName }

Get-ChildItem $Root -Recurse -File -Filter applicationHost.config -ErrorAction SilentlyContinue | ForEach-Object {
  "`n===== $($_.FullName)"
  [xml]$x = Get-Content $_.FullName -Raw
  "--- App pools"
  $x.configuration.'system.applicationHost'.applicationPools.add | ForEach-Object {
    $vars = @($_.environmentVariables.add | ForEach-Object { $_.name }) -join ','
    "{0} | runtime={1} | 32bit={2} | identity={3} | envVars={4}" -f $_.name, $_.managedRuntimeVersion, $_.enable32BitAppOnWin64, $_.processModel.identityType, $vars
  }
  "--- Sites, bindings, applications"
  $x.configuration.'system.applicationHost'.sites.site | ForEach-Object {
    "SITE {0} (id {1})" -f $_.name, $_.id
    $_.bindings.binding | ForEach-Object { "  binding {0} {1} sslFlags={2}" -f $_.protocol, $_.bindingInformation, $_.sslFlags }
    $_.application | ForEach-Object {
      $app = $_
      "  app {0} pool={1}" -f $app.path, $app.applicationPool
      $app.virtualDirectory | ForEach-Object { "    vdir {0} -> {1}" -f $_.path, $_.physicalPath }
    }
  }
}

Get-ChildItem $Root -Recurse -File -Filter web.config -ErrorAction SilentlyContinue | ForEach-Object {
  "`n===== $($_.FullName)"
  (Get-Content $_.FullName) -replace "($secret\s*=\s*`")[^`"]*", '$1***' -replace "($secret[^=]*=)[^;`"]*", '$1***' |
    Select-String -Pattern 'aspNetCore|handlers|processPath|arguments|hostingModel|appSettings|add key|rewrite|rule|ipSecurity|httpRedirect|authentication|environmentVariable' |
    ForEach-Object { $_.Line.Trim() }
}
