# VPN Manager (OpenVPN Hizli Arama) - uzaktan kurulum
#
#   irm https://raw.githubusercontent.com/bymrhan/vpn-manager/main/install.ps1 | iex
#
# Son surumun kurulum paketini GitHub Releases'ten indirir, SHA-256 ozetini dogrular ve kurar.
# Yonetici izni gerekmez; %LOCALAPPDATA%\Programs\OpenVPN Hizli Arama klasorune kurar.
# Belirli bir surum icin once:  $env:VPNM_VERSION = 'v2.0.0'
# (Bu dosya bilerek yalnizca ASCII karakter icerir: iex ile her PowerShell surumunde ayni calisir.)

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
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $kur
    if ($LASTEXITCODE -ne 0) { throw "Kurulum betigi $LASTEXITCODE koduyla bitti." }
    Write-Host ''
    Write-Host '  Hazir. Kaldirmak icin: %LOCALAPPDATA%\Programs\OpenVPN Hizli Arama\Kaldir.cmd' -ForegroundColor DarkGray
}
catch { Write-Host ('  Kurulum basarisiz: ' + $_.Exception.Message) -ForegroundColor Red }
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
