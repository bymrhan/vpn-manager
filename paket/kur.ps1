# OpenVPN Hızlı Arama — kurulum (yönetici izni gerekmez; kullanıcı klasörüne kurar)
#   Kur.cmd / install.ps1 : etkileşimli kurulum (aşağıdaki normal akış)
#   -Update               : uygulamanın kendi sessiz güncellemesi (yalnızca uygulama çağırır)
param(
    [switch]$Update,        # quiet update: no wizard, no window, no Start menu change
    [string]$InstallDir,    # update target (default: the normal install dir)
    [int]$WaitPid = 0,      # the running app; its exe is replaced only after it has exited
    [switch]$Background,    # restart the app with --background
    [string]$Version        # version being installed (for the failure marker); default: the new exe's version
)
$ErrorActionPreference = 'Stop'
$src = [IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path)

if ($Update) {
    # Quiet update, started hidden by the app after it verified this package's SHA-256.
    # Copies only the program files (uygulama\*, uninstaller). Settings, pins and history live in
    # %APPDATA%\OpenVpnQuickSearch and the profile pack in <dir>\profiller: none of them is touched.
    # Rollback: every file that is replaced is backed up first; if any copy fails, the backup is put
    # back and files new in this version are removed, so the old exe stays.
    # Once the app has exited, the finally block always restarts it, whatever failed.
    # A failed update leaves guncelleme-basarisiz.txt (version + UTC time): the app then does not
    # install that version automatically again for 24 h (no update / restart loop). Success deletes it.
    # Paths go through -LiteralPath / .NET APIs: a user profile path may contain [ ].
    $dst = if ($InstallDir) { $InstallDir.TrimEnd('\') } else { [IO.Path]::Combine($env:LOCALAPPDATA, 'Programs\OpenVPN Hizli Arama') }
    $exe = [IO.Path]::Combine($dst, 'OpenVPN-Hizli-Arama.exe')
    $log = [IO.Path]::Combine($dst, 'guncelleme.log')
    $marker = [IO.Path]::Combine($dst, 'guncelleme-basarisiz.txt')
    $staged = [IO.Path]::Combine($src, 'uygulama')
    function Log([string]$m) { try { [IO.File]::AppendAllText($log, (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $m + "`r`n", [Text.Encoding]::UTF8) } catch { } }
    # Checked before waiting: the helper then ends at once, and the app (still running) reports it.
    if (-not [IO.File]::Exists($exe)) { exit 2 }   # only an existing install is updated
    if (-not [IO.File]::Exists([IO.Path]::Combine($staged, 'OpenVPN-Hizli-Arama.exe'))) { Log 'paket eksik: uygulama\OpenVPN-Hizli-Arama.exe yok'; exit 3 }
    if (-not $Version) { try { $Version = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::Combine($staged, 'OpenVPN-Hizli-Arama.exe')).ProductVersion } catch { $Version = '?' } }
    Log "guncelleme basladi: $Version $src"
    # tells the app (UpdateClient.StartInstall) that the checks passed and this helper is waiting for it
    try { [IO.File]::WriteAllText([IO.Path]::Combine($src, '.bekliyor'), [string]$PID) } catch { }

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

    $ok = $false
    try {
        $files = @(Get-ChildItem -LiteralPath $staged -Recurse -File)
        foreach ($n in 'Kaldir.cmd', 'kaldir.ps1') { $f = [IO.Path]::Combine($src, $n); if ([IO.File]::Exists($f)) { $files += Get-Item -LiteralPath $f } }
        $backup = [IO.Path]::Combine($dst, '.guncelleme-yedek')
        if ([IO.Directory]::Exists($backup)) { [IO.Directory]::Delete($backup, $true) }
        [void][IO.Directory]::CreateDirectory($backup)
        $plan = @(foreach ($f in $files) {
            $rel = if ($f.FullName.StartsWith($staged + '\', [StringComparison]::OrdinalIgnoreCase)) { $f.FullName.Substring($staged.Length + 1) } else { $f.Name }
            $target = [IO.Path]::Combine($dst, $rel)
            [pscustomobject]@{ Source = $f.FullName; Target = $target; Backup = [IO.Path]::Combine($backup, $rel); Existed = [IO.File]::Exists($target); Touched = $false }
        })
        $copied = $false
        try {
            foreach ($x in $plan) { if ($x.Existed) { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($x.Backup)); [IO.File]::Copy($x.Target, $x.Backup, $true) } }
            foreach ($x in $plan) { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($x.Target)); $x.Touched = $true; [IO.File]::Copy($x.Source, $x.Target, $true) }
            $copied = $true
        }
        catch {
            Log ('kopyalama basarisiz, eski surum geri yukleniyor: ' + $_.Exception.Message)
            foreach ($x in $plan | Where-Object { $_.Touched }) {
                try {
                    if ($x.Existed) { [IO.File]::Copy($x.Backup, $x.Target, $true) }
                    elseif ([IO.File]::Exists($x.Target)) { [IO.File]::Delete($x.Target) }
                } catch { Log ('geri yuklenemedi (dosya degismemis olabilir): ' + $x.Target + ' ' + $_.Exception.Message) }
            }
        }
        if ($copied) {
            try { [IO.Directory]::Delete($backup, $true) } catch { }
            $ok = $true
            Log 'guncellendi'
        }
    }
    catch { Log ('guncelleme basarisiz: ' + $_.Exception.Message) }
    finally {
        try {
            if ($ok) { if ([IO.File]::Exists($marker)) { [IO.File]::Delete($marker) } }
            else { [IO.File]::WriteAllText($marker, $Version + "`r`n" + (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture) + "`r`n") }
        } catch { Log ('isaret dosyasi yazilamadi: ' + $_.Exception.Message) }
        try {
            $psi = New-Object Diagnostics.ProcessStartInfo $exe
            $psi.WorkingDirectory = $dst; $psi.UseShellExecute = $false
            if ($Background) { $psi.Arguments = '--background' }
            [void][Diagnostics.Process]::Start($psi)
        } catch { Log ('uygulama yeniden baslatilamadi: ' + $_.Exception.Message) }
    }
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
