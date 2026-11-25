# Compiles the app sources with tests\Tests.cs into a temporary exe and runs it.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
# Exit code 0 = all tests passed. build.ps1 calls this before packaging.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ('ovpnqs-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $exe = Join-Path $work 'Tests.exe'
    $mods = Get-ChildItem (Join-Path $root 'modules\*.cs') | ForEach-Object FullName
    $out = & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /main:Tests "/out:$exe" `
      /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Accessibility.dll `
      (Join-Path $root 'OpenVpnQuickSearch.cs') @mods (Join-Path $PSScriptRoot 'Tests.cs')
    if ($LASTEXITCODE -ne 0) { $out | Where-Object { $_ -match 'error' } | Write-Host; throw 'testler derlenemedi' }
    & $exe
    $code = $LASTEXITCODE
    # install.ps1 update decision (offline, scratch folder in %TEMP%; runs in this process)
    & (Join-Path $PSScriptRoot 'install-update.tests.ps1')
    if ($LASTEXITCODE -ne 0 -and $code -eq 0) { $code = $LASTEXITCODE }
}
finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
exit $code
