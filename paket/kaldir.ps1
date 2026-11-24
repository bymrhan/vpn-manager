# OpenVPN Hızlı Arama — kaldırma. VPN uygulamalarındaki profillere dokunmaz.
$dst = Join-Path $env:LOCALAPPDATA 'Programs\OpenVPN Hizli Arama'
Write-Host ''
Write-Host '  OpenVPN Hızlı Arama kaldırılıyor...' -ForegroundColor Cyan

Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'OpenVPN-Hizli-Arama.exe' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 700

$links = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'OpenVPN Hızlı Arama.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'OpenVPN Hızlı Arama.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Startup')) 'OpenVPN Hızlı Arama İzleyici.lnk')
)
foreach ($l in $links) { if (Test-Path -LiteralPath $l) { Remove-Item -LiteralPath $l -Force } }

$data = Join-Path $env:APPDATA 'OpenVpnQuickSearch'
if (Test-Path $data) {
    $a = Read-Host '  Ayarlar, sabitlenenler ve bağlantı geçmişi de silinsin mi? (E/H)'
    if ($a -match '^[eEyY]') { Remove-Item $data -Recurse -Force }
}

# FortiClient passwords this app saved in Windows Credential Manager ("OpenVpnQuickSearch:forti:<tunnel>")
$creds = @(cmdkey /list | ForEach-Object { if ($_ -match 'target=(OpenVpnQuickSearch:forti:.+?)\s*$') { $Matches[1] } })
if ($creds.Count -gt 0) {
    $a = Read-Host "  Kayıtlı $($creds.Count) FortiClient parolası (Windows Kimlik Bilgileri) silinsin mi? (E/H)"
    if ($a -match '^[eEyY]') { foreach ($c in $creds) { cmdkey "/delete:$c" | Out-Null } }
}

# Program klasörünü bu betik bittikten sonra sil (betik o klasörden çalışıyor olabilir)
Start-Process -FilePath 'cmd.exe' -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$dst`"" -WindowStyle Hidden
Write-Host '  Kaldırıldı. (Yedekler Belgeler\OpenVPN Yedekleri klasöründe duruyor.)' -ForegroundColor Green
Start-Sleep 2
