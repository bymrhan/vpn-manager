# OpenVPN Hızlı Arama — kurulum (yönetici izni gerekmez; kullanıcı klasörüne kurar)
#   Kur.cmd / install.ps1 : etkileşimli kurulum (aşağıdaki normal akış)
#   -Update               : uygulamanın kendi sessiz güncellemesi (yalnızca uygulama çağırır)
param(
    [switch]$Update,        # quiet update: no wizard, no window, no Start menu change
    [string]$InstallDir,    # update target (default: the normal install dir)
    [int]$WaitPid = 0,      # the running app; its exe is replaced only after it has exited
    [switch]$Background     # restart the app with --background
)
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Update) {
    # Quiet update, started hidden by the app after it verified this package's SHA-256.
    # Copies only the program files (uygulama\*, uninstaller). Settings, pins and history live in
    # %APPDATA%\OpenVpnQuickSearch and the profile pack in <dir>\profiller: none of them is touched.
    # Rollback: every file that is replaced is backed up first; if any copy fails, the backup is put
    # back and files new in this version are removed, so the old exe stays. The app is restarted either way.
    $dst = if ($InstallDir) { $InstallDir.TrimEnd('\') } else { Join-Path $env:LOCALAPPDATA 'Programs\OpenVPN Hizli Arama' }
    $exe = Join-Path $dst 'OpenVPN-Hizli-Arama.exe'
    $log = Join-Path $dst 'guncelleme.log'
    function Log([string]$m) { try { Add-Content -LiteralPath $log -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $m) -Encoding UTF8 } catch { } }
    if (-not (Test-Path -LiteralPath $exe)) { exit 2 }   # only an existing install is updated
    $staged = Join-Path $src 'uygulama'
    if (-not (Test-Path -LiteralPath (Join-Path $staged 'OpenVPN-Hizli-Arama.exe'))) { Log 'paket eksik: uygulama\OpenVPN-Hizli-Arama.exe yok'; exit 3 }
    Log "guncelleme basladi: $src"

    # wait for the app to exit (it exits right after starting this script)
    if ($WaitPid -gt 0) {
        $proc = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
        if ($proc) {
            if (-not $proc.WaitForExit(30000)) {
                # still there after 30 s: stop it only if it really is this install's exe
                $path = $null; try { $path = $proc.Path } catch { }
                if ($path -and ([IO.Path]::GetFullPath($path) -ieq [IO.Path]::GetFullPath($exe))) { Stop-Process -Id $WaitPid -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
            }
        }
    }

    $files = @(Get-ChildItem -LiteralPath $staged -Recurse -File) + @(Get-Item -LiteralPath (Join-Path $src 'Kaldir.cmd'), (Join-Path $src 'kaldir.ps1') -ErrorAction SilentlyContinue)
    $backup = Join-Path $dst '.guncelleme-yedek'
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $plan = foreach ($f in $files) {
        $rel = if ($f.FullName.StartsWith($staged + '\', [StringComparison]::OrdinalIgnoreCase)) { $f.FullName.Substring($staged.Length + 1) } else { $f.Name }
        [pscustomobject]@{ Source = $f.FullName; Target = (Join-Path $dst $rel); Backup = (Join-Path $backup $rel); Existed = (Test-Path -LiteralPath (Join-Path $dst $rel)); Touched = $false }
    }
    $ok = $true
    try {
        foreach ($x in $plan) { if ($x.Existed) { New-Item -ItemType Directory -Force -Path (Split-Path $x.Backup) | Out-Null; Copy-Item -LiteralPath $x.Target -Destination $x.Backup -Force } }
        foreach ($x in $plan) { New-Item -ItemType Directory -Force -Path (Split-Path $x.Target) | Out-Null; $x.Touched = $true; Copy-Item -LiteralPath $x.Source -Destination $x.Target -Force }
        Log 'guncellendi'
    }
    catch {
        $ok = $false
        Log ('kopyalama basarisiz, eski surum geri yukleniyor: ' + $_.Exception.Message)
        foreach ($x in $plan | Where-Object { $_.Touched }) {
            try {
                if ($x.Existed) { Copy-Item -LiteralPath $x.Backup -Destination $x.Target -Force }
                elseif (Test-Path -LiteralPath $x.Target) { Remove-Item -LiteralPath $x.Target -Force }
            } catch { Log ('geri yuklenemedi (dosya degismemis olabilir): ' + $x.Target + ' ' + $_.Exception.Message) }
        }
    }
    if ($ok) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
    try {
        if ($Background) { Start-Process -FilePath $exe -ArgumentList '--background' -WorkingDirectory $dst }
        else { Start-Process -FilePath $exe -WorkingDirectory $dst }
    } catch { Log ('uygulama yeniden baslatilamadi: ' + $_.Exception.Message) }
    if ($ok) { exit 0 } else { exit 1 }
}
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
