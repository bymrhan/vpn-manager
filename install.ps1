# VPN Manager (OpenVPN Hizli Arama) - uzaktan kurulum
#
#   irm https://raw.githubusercontent.com/bymrhan/vpn-manager/main/install.ps1 | iex
#
# Son surumun kurulum paketini GitHub Releases'ten indirir, SHA-256 ozetini dogrular ve kurar.
# Yonetici izni gerekmez; %LOCALAPPDATA%\Programs\OpenVPN Hizli Arama klasorune kurar.
# Belirli bir surum icin once:  $env:VPNM_VERSION = 'v2.0.0'
# (Bu dosya bilerek yalnizca ASCII karakter icerir: iex ile her PowerShell surumunde ayni calisir.)
#
# Mevcut kurulum varsa (kurulum klasorunde OpenVPN-Hizli-Arama.exe) ve indirilen paketin kur.ps1'i
# -Update destekliyorsa sihirbaz acilmadan sessiz guncelleme yapilir; ayarlar, sabitlenenler ve
# gecmis korunur. Aksi halde (ilk kurulum ya da -Update bilmeyen eski paket) etkilesimli kurulum.

# --- Karar ve yardimci fonksiyonlar (tests\install-update.tests.ps1 bunlari bu dosyadan ayiklayip dener) ---

# Parameter names of the package's kur.ps1. The file is only parsed, never run here.
function Get-VpnmKurParameters([string]$Kur) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Kur, [ref]$tokens, [ref]$errors)
    if (-not $ast.ParamBlock) { return @() }
    @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
}

# 'update' only when this install exists AND the package's kur.ps1 knows -Update; otherwise 'install'.
function Get-VpnmInstallMode([string]$InstallDir, [string]$Kur) {
    if (-not [IO.File]::Exists([IO.Path]::Combine($InstallDir, 'OpenVPN-Hizli-Arama.exe'))) { return 'install' }
    $names = Get-VpnmKurParameters $Kur
    foreach ($n in 'Update', 'InstallDir', 'Background') { if ($names -notcontains $n) { return 'install' } }
    'update'
}

# Arguments for the quiet update. -Version only when the tag is known and kur.ps1 accepts it.
function Get-VpnmUpdateArguments([string]$Kur, [string]$InstallDir, [string]$Tag) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Kur, '-Update', '-Background', '-InstallDir', $InstallDir)
    $v = ([string]$Tag).Trim() -replace '^[vV]', ''
    if ($v -and ((Get-VpnmKurParameters $Kur) -contains 'Version')) { $a += @('-Version', $v) }
    $a
}

# Processes of exactly this exe (full path, case-insensitive). Never matches by name alone.
function Select-VpnmAppProcess($Processes, [string]$Exe) {
    $full = [IO.Path]::GetFullPath($Exe)
    @($Processes | Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath([string]$_.ExecutablePath) -ieq $full) })
}

# Ends the running app of this install. Its only own exit is the tray menu "Cikis"; a close request
# (CloseMainWindow) just hides it to the tray, so whatever is still running after the wait is
# stopped. Settings are saved on every change, so a forced stop loses nothing. Returns the count.
function Stop-VpnmApp([string]$Exe, [int]$WaitSeconds = 3) {
    $all = try { @(Get-CimInstance Win32_Process -Filter "Name = 'OpenVPN-Hizli-Arama.exe'" -ErrorAction Stop) }
           catch { @(Get-Process -Name 'OpenVPN-Hizli-Arama' -ErrorAction SilentlyContinue | ForEach-Object { $p = $null; try { $p = $_.Path } catch { }; [pscustomobject]@{ ProcessId = $_.Id; ExecutablePath = $p } }) }
    $procs = Select-VpnmAppProcess $all $Exe
    if (-not $procs.Count) { return 0 }
    foreach ($p in $procs) { try { [void](Get-Process -Id $p.ProcessId -ErrorAction Stop).CloseMainWindow() } catch { } }
    $until = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $until -and @($procs | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }).Count) { Start-Sleep -Milliseconds 250 }
    foreach ($p in $procs) {
        # re-check the path: the PID may have been reused after the app exited during the wait
        $g = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
        $path = $null; if ($g) { try { $path = $g.Path } catch { } }
        if ($path -and ([IO.Path]::GetFullPath($path) -ieq [IO.Path]::GetFullPath($Exe))) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Milliseconds 500
    $procs.Count
}

# Runs the package's kur.ps1 in a separate powershell.exe and returns its exit code.
function Invoke-VpnmKur([string[]]$Arguments) {
    & powershell.exe @Arguments | Out-Host
    $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = 'bymrhan/vpn-manager'
$asset = 'OpenVPN-Hizli-Arama-Kurulum.zip'
$tag = $env:VPNM_VERSION
$base = if ($tag) { "https://github.com/$repo/releases/download/$tag" } else { "https://github.com/$repo/releases/latest/download" }

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) { Write-Host '  Bu uygulama yalnizca Windows icindir.' -ForegroundColor Red; return }

$work = Join-Path $env:TEMP ('vpn-manager-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    Write-Host ''
    Write-Host '  VPN Manager indiriliyor...' -ForegroundColor Cyan
    $zip = Join-Path $work $asset
    Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $zip
    $sumText = (Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset.sha256").Content
    if ($sumText -is [byte[]]) { $sumText = [Text.Encoding]::ASCII.GetString($sumText) }
    $expected = ([regex]::Match($sumText, '[0-9a-fA-F]{64}')).Value.ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) { throw "SHA-256 dogrulanamadi (beklenen $expected, bulunan $actual). Kurulum durduruldu." }
    Write-Host '  SHA-256 dogrulandi.' -ForegroundColor Green

    $dir = Join-Path $work 'paket'
    Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force
    $kur = Join-Path $dir 'kur.ps1'
    if (-not (Test-Path -LiteralPath $kur)) { throw 'Paket beklenmeyen bicimde: kur.ps1 yok.' }
    $installDir = [IO.Path]::Combine($env:LOCALAPPDATA, 'Programs\OpenVPN Hizli Arama')
    if ((Get-VpnmInstallMode $installDir $kur) -eq 'update') {
        # Existing install: quiet in-place update (no wizard, no Start menu change).
        Write-Host '  Mevcut kurulum bulundu: sessiz guncelleme (ayarlar, sabitlenenler ve gecmis korunur).' -ForegroundColor Cyan
        $n = Stop-VpnmApp ([IO.Path]::Combine($installDir, 'OpenVPN-Hizli-Arama.exe'))
        if ($n) { Write-Host '  Calisan uygulama kapatildi.' }
        $code = Invoke-VpnmKur (Get-VpnmUpdateArguments $kur $installDir $tag)
        if ($code -eq 1) { throw "Guncelleme yapilamadi; eski surum yerinde birakildi ve yeniden baslatildi. Ayrinti: $installDir\guncelleme.log" }
        if ($code -ne 0) {
            # kur.ps1 stopped before touching any file (2: no install, 3: package incomplete), so it did
            # not restart the app either: start the old version again if it was running.
            $exeOld = [IO.Path]::Combine($installDir, 'OpenVPN-Hizli-Arama.exe')
            if ($n -and [IO.File]::Exists($exeOld)) { try { Start-Process -FilePath $exeOld -ArgumentList '--background' -WorkingDirectory $installDir } catch { } }
            throw "Guncelleme betigi $code koduyla bitti."
        }
        Write-Host '  Guncellendi. Uygulama arka planda (bildirim alaninda) yeniden baslatildi.' -ForegroundColor Green
        Write-Host '  Bundan sonraki guncellemeler uygulamanin guncelleme ayarlarina gore otomatik gelir.' -ForegroundColor DarkGray
    }
    else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $kur
        if ($LASTEXITCODE -ne 0) { throw "Kurulum betigi $LASTEXITCODE koduyla bitti." }
        Write-Host ''
        Write-Host '  Hazir. Kaldirmak icin: %LOCALAPPDATA%\Programs\OpenVPN Hizli Arama\Kaldir.cmd' -ForegroundColor DarkGray
    }
}
catch { Write-Host ('  Kurulum basarisiz: ' + $_.Exception.Message) -ForegroundColor Red }
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
