# Builds tests\manual\FortiLive.cs and runs one command against the FortiClient window.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\manual\run-forti-live.ps1 probe "SANBOR-YENİ"
# "connect" and "disconnect" open or close a REAL VPN tunnel. See FortiLive.cs for all commands.
param([Parameter(Mandatory = $true)][string]$Command, [string]$Tunnel = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $env:TEMP 'ovpnqs-FortiLive.exe'
$mods = Get-ChildItem (Join-Path $root 'modules\*.cs') | ForEach-Object FullName
$out = & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /main:FortiLive "/out:$exe" `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Accessibility.dll `
  (Join-Path $root 'OpenVpnQuickSearch.cs') @mods (Join-Path $PSScriptRoot 'FortiLive.cs')
if ($LASTEXITCODE -ne 0) { $out | Where-Object { $_ -match 'error' } | Write-Host; throw 'derlenemedi' }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
& $exe $Command $Tunnel
exit $LASTEXITCODE
