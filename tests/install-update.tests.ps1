# Offline tests for install.ps1's update decision (no network, no real install dir, no real app).
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\install-update.tests.ps1
# The helper functions are taken from install.ps1 by parsing it; install.ps1 itself is never run.
# Everything happens in a scratch folder under %TEMP%. Exit code 0 = all passed.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installPs1 = Join-Path $root 'install.ps1'
$script:fails = 0; $script:count = 0
function Check([bool]$ok, [string]$name) {
    $script:count++
    if (-not $ok) { $script:fails++; Write-Host "  FAIL: $name" -ForegroundColor Red }
}

# load only the function definitions of install.ps1
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installPs1, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'install.ps1 parses'
$fns = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
foreach ($f in $fns) { . ([scriptblock]::Create($f.Extent.Text)) }
foreach ($n in 'Get-VpnmKurParameters', 'Get-VpnmInstallMode', 'Get-VpnmUpdateArguments', 'Select-VpnmAppProcess', 'Stop-VpnmApp', 'Invoke-VpnmKur') {
    Check ([bool](Get-Command $n -CommandType Function -ErrorAction SilentlyContinue)) "function $n defined"
}

# install.ps1 stays ASCII-only (it is fed through iex) and still verifies SHA-256 before expanding
$bytes = [IO.File]::ReadAllBytes($installPs1)
Check (-not ($bytes | Where-Object { $_ -gt 127 })) 'install.ps1 is ASCII-only'
$text = [IO.File]::ReadAllText($installPs1)
$iHash = $text.IndexOf('$expected -ne $actual'); $iExpand = $text.IndexOf('Expand-Archive'); $iMode = $text.IndexOf('(Get-VpnmInstallMode $installDir $kur)')
Check ($iHash -gt 0 -and $iExpand -gt $iHash -and $iMode -gt $iExpand) 'SHA-256 check before expand, expand before mode decision'
Check ($text -match '& powershell\.exe -NoProfile -ExecutionPolicy Bypass -File \$kur\r?\n') 'interactive kur.ps1 call unchanged'

$work = Join-Path $env:TEMP ('vpnm-install-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $pkgNew = Join-Path $work 'paket yeni'; $pkgOld = Join-Path $work 'paket eski'; $pkgFail = Join-Path $work 'paket hata'
    $inst = Join-Path $work 'Programs [x]\OpenVPN Hizli Arama'
    $empty = Join-Path $work 'bos kurulum'
    foreach ($d in $pkgNew, $pkgOld, $pkgFail, $inst, $empty) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    $exe = Join-Path $inst 'OpenVPN-Hizli-Arama.exe'
    [IO.File]::WriteAllText($exe, 'dummy')   # never started
    $record = Join-Path $work 'args.txt'
    # fake kur.ps1 with the update parameters: records how it was called
    $fake = @'
param([switch]$Update, [string]$InstallDir, [int]$WaitPid = 0, [switch]$Background, [string]$Version)
$lines = @("Update=$Update", "InstallDir=$InstallDir", "WaitPid=$WaitPid", "Background=$Background", "Version=$Version")
[IO.File]::WriteAllLines('RECORD', $lines)
exit CODE
'@
    [IO.File]::WriteAllText((Join-Path $pkgNew 'kur.ps1'), $fake.Replace('RECORD', $record).Replace('CODE', '0'))
    [IO.File]::WriteAllText((Join-Path $pkgFail 'kur.ps1'), $fake.Replace('RECORD', $record).Replace('CODE', '1'))
    # fake old kur.ps1 (2.0.0 style: no param block)
    [IO.File]::WriteAllText((Join-Path $pkgOld 'kur.ps1'), "`$dst = 'x'`r`nWrite-Host 'eski'`r`n")
    $kurNew = Join-Path $pkgNew 'kur.ps1'; $kurOld = Join-Path $pkgOld 'kur.ps1'; $kurFail = Join-Path $pkgFail 'kur.ps1'

    # parameter detection, including the real package kur.ps1 (parsed only)
    $p = Get-VpnmKurParameters $kurNew
    Check (($p -contains 'Update') -and ($p -contains 'Version')) 'fake new kur.ps1 parameters found'
    Check (@(Get-VpnmKurParameters $kurOld).Count -eq 0) 'old kur.ps1 has no parameters'
    $real = Get-VpnmKurParameters (Join-Path $root 'paket\kur.ps1')
    Check (($real -contains 'Update') -and ($real -contains 'InstallDir') -and ($real -contains 'Background') -and ($real -contains 'Version')) 'paket\kur.ps1 supports -Update -InstallDir -Background -Version'

    # mode decision
    Check ((Get-VpnmInstallMode $inst $kurNew) -eq 'update') 'existing install + new package: update'
    Check ((Get-VpnmInstallMode $inst $kurOld) -eq 'install') 'existing install + old package: interactive install'
    Check ((Get-VpnmInstallMode $empty $kurNew) -eq 'install') 'folder without exe: interactive install'
    Check ((Get-VpnmInstallMode (Join-Path $work 'yok') $kurNew) -eq 'install') 'no install folder: interactive install'
    $partial = Join-Path $work 'paket kismi'; New-Item -ItemType Directory -Force -Path $partial | Out-Null
    [IO.File]::WriteAllText((Join-Path $partial 'kur.ps1'), "param([switch]`$Update)`r`n")
    Check ((Get-VpnmInstallMode $inst (Join-Path $partial 'kur.ps1')) -eq 'install') 'kur.ps1 with -Update only (no -InstallDir/-Background): interactive install'

    # arguments
    $a = Get-VpnmUpdateArguments $kurNew $inst 'v2.1.0'
    Check (($a -join '|') -eq ("-NoProfile|-ExecutionPolicy|Bypass|-File|$kurNew|-Update|-Background|-InstallDir|$inst|-Version|2.1.0")) 'update arguments with tag'
    Check (-not ((Get-VpnmUpdateArguments $kurNew $inst '') -contains '-Version')) 'no tag: no -Version'
    Check (-not ((Get-VpnmUpdateArguments $kurNew $inst $null) -contains '-Version')) 'null tag: no -Version'
    Check (-not ((Get-VpnmUpdateArguments (Join-Path $partial 'kur.ps1') $inst 'v2.1.0') -contains '-Version')) 'kur.ps1 without -Version: not passed'

    # running the fake kur.ps1 the way install.ps1 does (paths with spaces and [ ]); one child
    # powershell.exe only, to keep run-tests.ps1 fast. The fake exits 1, so a nonzero code must come back.
    $code = Invoke-VpnmKur (Get-VpnmUpdateArguments $kurFail $inst 'v2.1.0')
    Check ($code -eq 1) 'kur.ps1 exit code returned'
    $rec = if (Test-Path -LiteralPath $record) { [IO.File]::ReadAllLines($record) } else { @() }
    Check (($rec -join '|') -eq ("Update=True|InstallDir=$inst|WaitPid=0|Background=True|Version=2.1.0")) 'fake kur.ps1 got -Update -Background -InstallDir -Version, no -WaitPid'
    Check ([IO.File]::ReadAllText($exe) -eq 'dummy') 'dummy exe untouched'

    # process selection: exact path only, never by name alone
    $procs = @(
        [pscustomobject]@{ ProcessId = 1; ExecutablePath = $exe },
        [pscustomobject]@{ ProcessId = 2; ExecutablePath = $exe.ToUpperInvariant() },
        [pscustomobject]@{ ProcessId = 3; ExecutablePath = (Join-Path $empty 'OpenVPN-Hizli-Arama.exe') },
        [pscustomobject]@{ ProcessId = 4; ExecutablePath = $null })
    $sel = @(Select-VpnmAppProcess $procs $exe)
    Check ((($sel | ForEach-Object { $_.ProcessId }) -join ',') -eq '1,2') 'only processes of this exe path are selected'
    Check (@(Select-VpnmAppProcess @() $exe).Count -eq 0) 'no processes: nothing selected'
    # nothing runs from the scratch folder, so this stops nothing
    Check ((Stop-VpnmApp $exe 0) -eq 0) 'Stop-VpnmApp on a scratch exe stops nothing'
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

if ($script:fails) { Write-Host "  install.ps1 tests: $($script:fails) of $($script:count) failed" -ForegroundColor Red; exit 1 }
Write-Host "  install.ps1 tests: $($script:count) passed"
exit 0
