# Adds FortiClient SSL VPN tunnels from FortiClient-Tunelleri.json (run as administrator).
# Only tunnel name, gateway address and connection options are written; usernames and
# passwords are not part of the pack and are entered in FortiClient on first connect.
# Existing tunnels with the same name are left untouched.
param([Parameter(Mandatory = $true)][string]$Json)

$ErrorActionPreference = 'Stop'
$base = 'HKLM:\SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels'
$log = Join-Path $env:TEMP 'FortiClient-Ice-Aktar.log'
"$(Get-Date -Format s) start" | Out-File $log -Encoding utf8

try {
    if (-not (Test-Path 'HKLM:\SOFTWARE\Fortinet\FortiClient')) { "FortiClient kurulu değil" | Out-File $log -Append; exit 2 }
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }
    $tunnels = Get-Content -Raw -Encoding UTF8 $Json | ConvertFrom-Json
    $added = 0
    foreach ($t in $tunnels) {
        $key = Join-Path $base $t.name
        if (Test-Path $key) { "skip $($t.name)" | Out-File $log -Append; continue }
        New-Item -Path $key -Force | Out-Null
        New-ItemProperty -Path $key -Name 'Server' -Value $t.server -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $key -Name 'ServerSorted' -Value $t.server -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $key -Name 'Description' -Value '' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $key -Name 'ServerCert' -Value ([string]$t.serverCert) -PropertyType String -Force | Out-Null
        foreach ($n in 'promptusername', 'promptcertificate', 'dual_stack', 'sso_enabled', 'use_external_browser', 'azure_auto_login') {
            $v = 0; if ($t.options.PSObject.Properties.Name -contains $n) { $v = [int]$t.options.$n }
            New-ItemProperty -Path $key -Name $n -Value $v -PropertyType DWord -Force | Out-Null
        }
        $added++
        "added $($t.name)" | Out-File $log -Append
    }
    "done, added $added" | Out-File $log -Append
    exit 0
}
catch {
    "error: $($_.Exception.Message)" | Out-File $log -Append
    exit 1
}
