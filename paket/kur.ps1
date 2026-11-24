# OpenVPN Hızlı Arama — kurulum (yönetici izni gerekmez; kullanıcı klasörüne kurar)
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dst = Join-Path $env:LOCALAPPDATA 'Programs\OpenVPN Hizli Arama'
$exe = Join-Path $dst 'OpenVPN-Hizli-Arama.exe'

Write-Host ''
Write-Host '  OpenVPN Hızlı Arama kuruluyor...' -ForegroundColor Cyan

# .NET Framework 4.8 (Windows 10/11 ile gelir)
$rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if (-not $rel -or $rel -lt 528040) { Write-Host '  .NET Framework 4.8 gerekli: https://dotnet.microsoft.com/download/dotnet-framework/net48' -ForegroundColor Yellow; Read-Host '  Çıkmak için Enter'; exit 1 }

# çalışan eski sürümü kapat
Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'OpenVPN-Hizli-Arama.exe' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item (Join-Path $src 'uygulama\*') $dst -Recurse -Force
if (Test-Path (Join-Path $src 'profiller')) { Copy-Item (Join-Path $src 'profiller') $dst -Recurse -Force }
Copy-Item (Join-Path $src 'Kaldir.cmd'), (Join-Path $src 'kaldir.ps1') $dst -Force

# Başlat menüsü kısayolu
$ws = New-Object -ComObject WScript.Shell
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'OpenVPN Hızlı Arama.lnk'
$s = $ws.CreateShortcut($menu); $s.TargetPath = $exe; $s.WorkingDirectory = $dst; $s.IconLocation = "$exe,0"; $s.Description = 'OpenVPN ve FortiClient profillerini hızlıca ara ve bağlan'; $s.Save()

Write-Host "  Kuruldu: $dst" -ForegroundColor Green
Write-Host '  İlk kurulum sihirbazı açılıyor...'
# --setup: settings dosyası olsa bile sihirbazı göster (paket içe aktarma için)
Start-Process -FilePath $exe -ArgumentList '--setup' -WorkingDirectory $dst
