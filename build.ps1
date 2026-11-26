# Builds OpenVPN-Hizli-Arama.exe and the installer zip.
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-NoProfiles]
# -NoProfiles: package without this PC's VPN profiles (generic installer). Only this build writes
# a .sha256 and the release copy (outputs\release\). Without it the zip carries this PC's profiles
# with their private keys: it gets a "do not share" name and no .sha256, so it cannot pass for a
# release asset.
param([switch]$NoProfiles)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# <zip>.sha256 next to a package, in the release asset's format ("<sha256>  <file name>" + LF, as
# sha256sum writes it). install.ps1 and the in-app updater refuse a package without a matching hash.
function Write-Sha256([string]$file, [string]$name) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$file.sha256", "$hash  $name`n", (New-Object Text.UTF8Encoding $false))
    return $hash
}
$Out = [IO.Path]::GetFullPath((Join-Path $root '..\..\outputs'))
$stage = Join-Path $env:TEMP ('ovpnqs-build-' + [guid]::NewGuid().ToString('N'))
$appDir = Join-Path $stage 'uygulama'; $packDir = Join-Path $stage 'profiller'
# Issue token resource (step 1a): its own temp folder, outside $stage (everything in $stage is zipped).
$tokenDir = Join-Path $env:TEMP ('ovpnqs-token-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
# The staging folder holds this PC's profiles (private keys): it is removed even when a step fails.
try {

# The token variable is read once and removed from this process at once, so no child process
# (tests, csc, git) inherits it.
$envToken = $env:VPNM_ISSUE_TOKEN
Remove-Item Env:VPNM_ISSUE_TOKEN -ErrorAction SilentlyContinue

# secrets\ must never be in git: stop before anything else.
$tracked = $null
$tokenFile = Join-Path $root 'secrets\issue-token.txt'
if (Test-Path -LiteralPath $tokenFile) {
    # fail closed: with a token file present, git must be able to say it is not tracked
    $inRepo = $null
    try { $inRepo = & git -C $root rev-parse --is-inside-work-tree 2>$null } catch { }
    if ($inRepo -ne 'true') { throw 'secrets\issue-token.txt var ama git denetlenemiyor (git yok ya da depo değil): anahtar gömülmez.' }
}
try { $tracked = & git -C $root ls-files -- secrets 2>$null } catch { }
if ($tracked) { throw 'secrets\ klasörü git tarafından izleniyor: anahtar gömülmez. Dosyayı depodan çıkarın (git rm --cached) ve anahtarı GitHub''da iptal edin.' }

# 0) automated tests (tests\run-tests.ps1): no package from a failing build
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\run-tests.ps1')
if ($LASTEXITCODE -ne 0) { throw 'testler başarısız; paket üretilmedi' }

# 1) compile
$exe = Join-Path $appDir 'OpenVPN-Hizli-Arama.exe'
$mods = Get-ChildItem (Join-Path $root 'modules\*.cs') | ForEach-Object FullName
$svgRes = Get-ChildItem (Join-Path $root 'assets\*') -Include *.svg,*.png | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
function Invoke-Compile([string[]]$extra) {
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ "/out:$exe" "/win32icon:$root\app.ico" "/resource:$root\app.ico,app.ico" $svgRes $extra `
      /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Accessibility.dll `
      (Join-Path $root 'OpenVpnQuickSearch.cs') @mods | Where-Object { $_ -notmatch 'warning CS0(649|169|219|168)' }
    if ($LASTEXITCODE -ne 0) { throw 'derleme başarısız' }
}
Invoke-Compile @()

# 1a) issue token for direct error reports (modules\IssueSender.cs, docs\RELEASING.md): first line of
# secrets\issue-token.txt, else $env:VPNM_ISSUE_TOKEN. The app's own IssueToken.Encode (loaded from the
# exe just built) turns it into the resource "issue.token"; that encoding only keeps the token out of a
# plain string scan of the exe, it is NOT protection. The exe is then built again with the resource.
# The value is never printed or put in a message. Without a token the first build is the result.
$token = $null
if (Test-Path -LiteralPath $tokenFile) { $token = [string](Get-Content -LiteralPath $tokenFile -TotalCount 1) }
if ($token) { $token = $token.Trim() }
if (-not $token -and $envToken) { $token = ([string]$envToken).Trim() }   # no file or empty first line: the variable
$envToken = $null
$embedded = $false
if ($token) {
    $issueToken = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe)).GetType('IssueToken')
    if (-not $issueToken.GetMethod('LooksLikeToken').Invoke($null, @($token))) { $token = $null; throw 'issue token: GitHub anahtarı biçiminde değil' }
    New-Item -ItemType Directory -Force -Path $tokenDir | Out-Null
    $tokenPath = Join-Path $tokenDir 'issue.token'
    [IO.File]::WriteAllBytes($tokenPath, [byte[]]$issueToken.GetMethod('Encode').Invoke($null, @($token)))
    $token = $null
    Invoke-Compile @("/resource:$tokenPath,issue.token")
    Remove-Item $tokenDir -Recurse -Force -ErrorAction SilentlyContinue
    $embedded = $true
}
Write-Host ('  token embedded: ' + $(if ($embedded) { 'yes' } else { 'no' }))
Copy-Item (Join-Path $root 'paket/OpenVPN-Hizli-Arama.exe.config') $appDir
Copy-Item (Join-Path $root 'paket/OpenVPN-Hizli-Arama-Kullanim.txt') $appDir

# 2) profile pack from this PC
if (-not $NoProfiles) {
    New-Item -ItemType Directory -Force -Path $packDir | Out-Null
    Add-Type -TypeDefinition @"
using System; using System.Reflection; using System.Collections.Generic;
public static class PackBuilder {
  public static string Backup(string exe, string folder) {
    var a = Assembly.Load(System.IO.File.ReadAllBytes(exe)); // from bytes: no file lock, no temp copy
    var pins = a.GetType("PinStore").GetMethod("Load").Invoke(null, null);
    var args = new object[] { folder, pins, 0 };
    var zip = (string)a.GetType("BackupService").GetMethod("CreateBackup").Invoke(null, args);
    return zip + "|" + args[2];
  }
}
"@
    $r = [PackBuilder]::Backup($exe, $packDir).Split('|')
    Move-Item $r[0] (Join-Path $packDir 'OpenVPN-Profilleri.zip')
    Write-Host "  OpenVPN profilleri: $($r[1])"

    # FortiClient SSL VPN tunnels: name, gateway and options only (no DATA* credentials, no descriptions)
    $tunnels = @()
    $base = 'HKLM:\SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels'
    if (Test-Path $base) {
        foreach ($k in Get-ChildItem $base) {
            $key = Get-Item $k.PSPath; $server = $key.GetValue('Server')
            if (-not $server) { continue }
            $opt = [ordered]@{}
            foreach ($n in 'promptusername', 'promptcertificate', 'dual_stack', 'sso_enabled', 'use_external_browser', 'azure_auto_login') { $v = $key.GetValue($n); if ($v -ne $null) { $opt[$n] = [int]$v } }
            $tunnels += [ordered]@{ name = $k.PSChildName; server = [string]$server; serverCert = [string]$key.GetValue('ServerCert', '1'); options = $opt }
        }
    }
    [IO.File]::WriteAllText((Join-Path $packDir 'FortiClient-Tunelleri.json'), (ConvertTo-Json @($tunnels) -Depth 4), (New-Object Text.UTF8Encoding $false))
    Write-Host "  FortiClient tünelleri: $($tunnels.Count)"
    $s = [IO.File]::ReadAllText((Join-Path $root 'paket\FortiClient-Ice-Aktar.ps1')); [IO.File]::WriteAllText((Join-Path $packDir 'FortiClient-Ice-Aktar.ps1'), $s, (New-Object Text.UTF8Encoding $true))
}

# 3) installer scripts (PowerShell 5.1 needs UTF-8 with BOM for Turkish text)
foreach ($n in 'kur.ps1', 'kaldir.ps1') { $s = [IO.File]::ReadAllText((Join-Path $root "paket\$n")); [IO.File]::WriteAllText((Join-Path $stage $n), $s, (New-Object Text.UTF8Encoding $true)) }
foreach ($n in 'Kur.cmd', 'Kaldir.cmd', 'BENIOKU.txt') { Copy-Item (Join-Path $root "paket\$n") $stage }

# 4) zip
$zip = Join-Path $Out $(if ($NoProfiles) { 'OpenVPN-Hizli-Arama-Kurulum-Profilsiz.zip' } else { 'OpenVPN-Hizli-Arama-PROFILLI-PAYLASMAYIN.zip' })
if (Test-Path $zip) { Remove-Item $zip -Force }
if (Test-Path "$zip.sha256") { Remove-Item "$zip.sha256" -Force }
# Both assemblies: ZipArchive lives in System.IO.Compression. Without -NoProfiles it happened to
# be loaded already by the profile-pack step, so -NoProfiles failed with "Unable to find type".
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# entry names with "/" separators (ZipFile.CreateFromDirectory on .NET Framework writes "\")
$fs = [IO.File]::Open($zip, 'CreateNew'); $archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
foreach ($file in Get-ChildItem $stage -Recurse -File) {
    $rel = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
$archive.Dispose(); $fs.Dispose()
$sha = $null
if ($NoProfiles) {
    $sha = Write-Sha256 $zip (Split-Path -Leaf $zip)
    # Release copy under the asset name the updater and install.ps1 download (see docs\RELEASING.md).
    $rel = Join-Path $Out 'release'
    New-Item -ItemType Directory -Force -Path $rel | Out-Null
    $relZip = Join-Path $rel 'OpenVPN-Hizli-Arama-Kurulum.zip'
    Copy-Item $zip $relZip -Force
    Write-Sha256 $relZip 'OpenVPN-Hizli-Arama-Kurulum.zip' | Out-Null
    Write-Host "  Sürüm dosyaları: $rel"
}
try { Copy-Item $exe (Join-Path $Out 'OpenVPN-Hizli-Arama.exe') -Force }
catch { Write-Warning "outputs\OpenVPN-Hizli-Arama.exe güncellenmedi (uygulama çalışıyor olabilir). Paket yine de üretildi." }
Write-Host "  Paket: $zip"
if ($sha) { Write-Host "  SHA-256: $sha" }
else {
    Write-Warning "DİKKAT: bu paket bu bilgisayarın VPN profillerini ÖZEL ANAHTARLARIYLA içerir. PAYLAŞMAYIN, sürüme YÜKLEMEYİN. Dağıtım için: build.ps1 -NoProfiles"
    # An older build wrote the profile package under the release asset name: flag what is left of it.
    foreach ($old in 'OpenVPN-Hizli-Arama-Kurulum.zip', 'OpenVPN-Hizli-Arama-Kurulum.zip.sha256') {
        if (Test-Path (Join-Path $Out $old)) { Write-Warning "outputs\$old eski bir profilli derlemeden kalmış olabilir; yayınlamayın, silin." }
    }
}

}
finally {
    Remove-Item (Join-Path $stage 'profiller') -Recurse -Force -ErrorAction SilentlyContinue  # contains profile secrets
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $tokenDir -Recurse -Force -ErrorAction SilentlyContinue   # encoded issue token
}
