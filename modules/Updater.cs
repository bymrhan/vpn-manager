using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// =====================================================================
// Updates from the GitHub Releases of bymrhan/vpn-manager.
//   UpdateLogic  - pure decisions (versions, release JSON, due time, idle, install location); unit-tested.
//   UpdateClient - network and file work (check, download, SHA-256, extract, start the installer helper).
//   UpdateNotice - the non-modal glass notice for "ask" mode.
// Security: only HTTPS, only release assets of this repository, SHA-256 checked against the
// release's .sha256 asset before anything from the package runs. No user data is sent.
// =====================================================================

internal sealed class ReleaseInfo
{
    public string Tag, Version, HtmlUrl, Notes, ZipUrl, ShaUrl;
    public bool Prerelease, Draft;

    // Both assets present: the package can be downloaded and verified.
    public bool CanInstall { get { return ZipUrl != null && ShaUrl != null; } }
}

internal enum UpdateStage { Offer, Downloading, Verifying, Preparing, Waiting, Restarting, Failed }

internal static class UpdateLogic
{
    // "512 B" below 1 MB, else "4,2 MB" (tr-TR decimal comma, 1 decimal, MB = 1048576).
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        if (bytes < 1048576) return bytes.ToString(tr) + " B";
        return (bytes / 1048576.0).ToString("0.0", tr) + " MB";
    }

    // 0..100, or -1 when the total is unknown.
    public static int Percent(long done, long total)
    {
        if (total <= 0) return -1;
        if (done < 0) done = 0;
        if (done >= total) return 100;
        return (int)Math.Round(done * 100.0 / total, MidpointRounding.AwayFromZero);
    }

    // One-line status text for the update progress UI.
    public static string StageText(UpdateStage stage, long done, long total, string version)
    {
        switch (stage)
        {
            case UpdateStage.Offer: return "Sürüm " + version + " hazır";
            case UpdateStage.Downloading:
                var head = "Sürüm " + version + " indiriliyor · ";
                if (total <= 0) return head + FormatBytes(done);
                string size;
                if (total >= 1048576)
                    size = FormatBytes(done).Replace(" MB", "") + " / " + FormatBytes(total);
                else
                    size = FormatBytes(done) + " / " + FormatBytes(total);
                return head + size + " · %" + Percent(done, total).ToString(CultureInfo.InvariantCulture);
            case UpdateStage.Verifying: return "Doğrulanıyor…";
            case UpdateStage.Preparing: return "Paket hazırlanıyor…";
            case UpdateStage.Waiting: return "Uygulamanın kapanması bekleniyor…";
            case UpdateStage.Restarting: return "Yeniden başlatılıyor…";
            default: return "Güncelleme başarısız";
        }
    }

    // Why a requested install has to wait (busy wins over an open window); null when it can go ahead.
    public static string WaitReason(bool busy, bool dialogOpen)
    {
        if (busy) return "Bağlantı işlemi bitince kurulacak";
        if (dialogOpen) return "Açık pencereler kapanınca kurulacak";
        return null;
    }

    public const string Repo = "bymrhan/vpn-manager";
    public const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    public const string AssetPrefix = "https://github.com/" + Repo + "/releases/download/";
    public const string ZipAsset = "OpenVPN-Hizli-Arama-Kurulum.zip";
    public const string ShaAsset = ZipAsset + ".sha256";
    public const string UserAgent = "VpnManager-Updater";
    public const string ExeName = "OpenVPN-Hizli-Arama.exe";
    public const int IdleThresholdMs = 2 * 60 * 1000;             // no keyboard / mouse input for about 2 minutes
    public static readonly TimeSpan FailureRetry = TimeSpan.FromHours(1);

    // "2.0.0" from AssemblyInformationalVersion (modules\AssemblyInfo.cs), else the assembly version.
    public static string CurrentVersion
    {
        get
        {
            var asm = typeof(UpdateLogic).Assembly;
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
            int[] parts;
            if (info != null && TryParseVersion(info.InformationalVersion, out parts)) return Format(parts);
            var v = asm.GetName().Version;
            return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
        }
    }

    // %LOCALAPPDATA%\Programs\OpenVPN Hizli Arama (where install.ps1 / kur.ps1 install, no admin rights).
    public static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenVPN Hizli Arama"); }
    }

    // "v2.10.0", "2.1", "2.1.0.0" -> numbers (1 to 4 parts). Pre-release suffixes ("2.1.0-beta") are rejected.
    public static bool TryParseVersion(string text, out int[] parts)
    {
        parts = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
        var bits = t.Split('.');
        if (bits.Length < 1 || bits.Length > 4) return false;
        var result = new int[4];
        for (var i = 0; i < bits.Length; i++)
        {
            int n;
            if (bits[i].Length == 0 || bits[i].Length > 9 || !bits[i].All(char.IsDigit) || !int.TryParse(bits[i], NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false;
            result[i] = n;
        }
        parts = result;
        return true;
    }

    // "v2.1.0" -> "2.1.0"; null when the tag is not a plain version.
    public static string NormalizeTag(string tag)
    {
        int[] parts;
        return TryParseVersion(tag, out parts) ? Format(parts) : null;
    }

    static string Format(int[] p)
    {
        return p[3] != 0 ? p[0] + "." + p[1] + "." + p[2] + "." + p[3] : p[0] + "." + p[1] + "." + p[2];
    }

    // Numeric compare, part by part ("2.10.0" > "2.9.0"). Unparseable versions sort lowest.
    public static int CompareVersions(string a, string b)
    {
        int[] x, y;
        var okA = TryParseVersion(a, out x); var okB = TryParseVersion(b, out y);
        if (!okA || !okB) return okA == okB ? 0 : okA ? 1 : -1;
        for (var i = 0; i < 4; i++) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
        return 0;
    }

    public static bool IsNewer(string candidate, string current) { return CompareVersions(candidate, current) > 0 && NormalizeTag(candidate) != null; }

    // Release JSON (GitHub REST "release" object) -> ReleaseInfo. Null when the JSON or the tag is unusable.
    // Asset URLs are kept only when they are release assets of this repository over HTTPS.
    public static ReleaseInfo ParseRelease(string json, out string problem)
    {
        problem = null;
        Dictionary<string, object> d;
        try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json ?? ""); }
        catch { problem = "sürüm bilgisi okunamadı"; return null; }
        if (d == null) { problem = "sürüm bilgisi okunamadı"; return null; }
        var r = new ReleaseInfo
        {
            Tag = Str(d, "tag_name"),
            Notes = Str(d, "body") ?? "",
            Prerelease = Bool(d, "prerelease"),
            Draft = Bool(d, "draft")
        };
        r.Version = NormalizeTag(r.Tag);
        if (r.Version == null) { problem = "sürüm etiketi geçersiz"; return null; }
        var html = Str(d, "html_url");
        r.HtmlUrl = html != null && html.StartsWith(ReleasesPage + "/", StringComparison.Ordinal) ? html : ReleasesPage;
        object assets;
        if (d.TryGetValue("assets", out assets) && assets is IEnumerable && !(assets is string))
        {
            foreach (var o in (IEnumerable)assets)
            {
                var a = o as IDictionary<string, object>;
                if (a == null) continue;
                var name = Str(a, "name"); var url = Str(a, "browser_download_url");
                if (name == ZipAsset && IsAllowedAssetUrl(url, r.Tag, ZipAsset)) r.ZipUrl = url;
                else if (name == ShaAsset && IsAllowedAssetUrl(url, r.Tag, ShaAsset)) r.ShaUrl = url;
            }
        }
        return r;
    }

    // Only https://github.com/bymrhan/vpn-manager/releases/download/<tag>/<asset>.
    public static bool IsAllowedAssetUrl(string url, string tag, string asset)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(tag)) return false;
        return string.Equals(url, AssetPrefix + Uri.EscapeDataString(tag) + "/" + asset, StringComparison.Ordinal);
    }

    // Prereleases and drafts are never offered.
    public static bool IsOffered(ReleaseInfo r) { return r != null && !r.Prerelease && !r.Draft && r.Version != null; }

    // The release is worth telling the user about: offered, newer than this build, not skipped
    // (a manual check ignores the skipped version).
    public static bool ShouldOffer(ReleaseInfo r, string current, string skipped, bool manual)
    {
        if (!IsOffered(r) || !IsNewer(r.Version, current)) return false;
        return manual || string.IsNullOrEmpty(skipped) || CompareVersions(r.Version, skipped) != 0;
    }

    // "<64 hex>  name" (sha256sum format) or a bare hash -> lower-case hash; null when none.
    public static string ParseSha256(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static string Sha256Line(string hash, string fileName) { return hash.ToLowerInvariant() + "  " + fileName + "\n"; }

    // Is an automatic check due? "startup": once per run. "daily" / "weekly": since the last successful check.
    // A failed check is retried no sooner than FailureRetry (startup: at the next start).
    public static bool IsDue(string interval, DateTime lastCheckUtc, DateTime lastFailureUtc, bool checkedThisRun, DateTime nowUtc)
    {
        if (interval == "startup") return !checkedThisRun;
        if (lastFailureUtc > DateTime.MinValue && nowUtc >= lastFailureUtc && nowUtc - lastFailureUtc < FailureRetry) return false;
        if (lastCheckUtc <= DateTime.MinValue.AddYears(1)) return true;   // never checked
        if (lastCheckUtc > nowUtc) return true;                            // clock moved back
        return nowUtc - lastCheckUtc >= (interval == "weekly" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));
    }

    // Silent install only when nothing is going on: no connect / disconnect / import, no dialog open,
    // and the panel hidden or the user away from keyboard and mouse for IdleThresholdMs.
    // A connected VPN does not block: the VPN clients run on their own.
    public static bool IsIdle(bool windowVisible, long idleMs, bool busy, bool dialogOpen)
    {
        if (busy || dialogOpen) return false;
        return !windowVisible || idleMs >= IdleThresholdMs;
    }

    // Only a copy running from the install dir updates itself; a portable copy elsewhere just notifies.
    public static bool IsInstalledCopy(string exePath, string installDir)
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(installDir)) return false;
        try
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(exePath)).TrimEnd('\\');
            var want = Path.GetFullPath(installDir).TrimEnd('\\');
            return string.Equals(dir, want, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(exePath), ExeName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // kur.ps1 -Update writes this file into the install dir when an update fails (rolled back or not
    // applied): line 1 the version, line 2 the UTC time (yyyy-MM-ddTHH:mm:ssZ). Success deletes it.
    public const string FailureMarkerName = "guncelleme-basarisiz.txt";
    public static readonly TimeSpan FailureBlock = TimeSpan.FromHours(24);

    // True when the marker says this version failed less than 24 h ago: no automatic install of it
    // (the "ask" notice is shown instead), so a failing update cannot loop install -> rollback -> restart.
    // A marker with an unreadable time counts as fresh; one dated more than a day in the future is ignored.
    public static bool FailureMarkerBlocks(string markerText, string version, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(markerText) || string.IsNullOrEmpty(version)) return false;
        var lines = markerText.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || CompareVersions(lines[0].Trim(), version) != 0 || NormalizeTag(lines[0].Trim()) == null) return false;
        DateTime when;
        if (lines.Length < 2 || !DateTime.TryParseExact(lines[1].Trim(), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when)) return true;
        var age = nowUtc - when;
        return age < FailureBlock && age > -FailureBlock;
    }

    // guncellendi.txt (written by kur.ps1 -Update on success): line 1 the new version, line 2 the UTC time
    // (ISO 8601 'o'). True when line 1 is a plain version and line 2 a parsable time; the caller checks the age.
    public static bool ParseUpdatedMarker(string text, out string version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return false;
        var v = NormalizeTag(lines[0].Trim());
        DateTime when;
        if (v == null || !DateTime.TryParse(lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) return false;
        version = v;
        return true;
    }

    // First lines of the release notes for the notice (markdown markers removed).
    public static string NotesPreview(string notes, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#', '*', '-', ' ').Trim())
            .Where(l => l.Length > 0).Take(3);
        var s = string.Join(" · ", lines);
        return s.Length <= maxChars ? s : s.Substring(0, Math.Max(0, maxChars - 1)).TrimEnd() + "…";
    }

    static string Str(IDictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as string : null; }
    static bool Bool(IDictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
}

internal static class UpdateClient
{
    const int TimeoutMs = 10000;
    const long MaxZipBytes = 200L * 1024 * 1024, MaxShaBytes = 4096, MaxJsonBytes = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // Milliseconds since the last keyboard / mouse input in this session (0 when unknown).
    public static long IdleMilliseconds()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime);
        }
        catch { return 0; }
    }

    // Working folder for downloads: %TEMP%\VpnManager-Update\<random>.
    public static string WorkRoot { get { return Path.Combine(Path.GetTempPath(), "VpnManager-Update"); } }

    // Removes download folders older than an hour (a finished update leaves its package behind).
    public static void CleanupOld()
    {
        try
        {
            if (!Directory.Exists(WorkRoot)) return;
            foreach (var d in Directory.GetDirectories(WorkRoot))
                try { if ((DateTime.UtcNow - Directory.GetCreationTimeUtc(d)).TotalHours > 1) Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    // GET the latest release. Never call on the UI thread. Null + error on network / format problems.
    public static ReleaseInfo FetchLatest(out string error)
    {
        error = null;
        try
        {
            var json = Encoding.UTF8.GetString(Get(UpdateLogic.ApiUrl, MaxJsonBytes, "application/vnd.github+json"));
            string problem;
            var r = UpdateLogic.ParseRelease(json, out problem);
            if (r == null) error = problem;
            return r;
        }
        catch (Exception ex) { error = Describe(ex); return null; }
    }

    // Pause between zip download attempts (3 attempts in total). Kept as a field so the schedule is easy to read.
    static readonly int[] RetryWaitsMs = { 1000, 3000 };
    public const string CancelledText = "iptal edildi";

    // Downloads the package and its .sha256, verifies and extracts it. Never call on the UI thread.
    // report(stage, done, total) and cancelled() may be null; report runs on the calling (worker) thread.
    // Returns the extracted package folder (contains kur.ps1), or null + error (CancelledText when cancelled).
    // Nothing runs here.
    public static string Download(ReleaseInfo r, Action<UpdateStage, long, long> report, Func<bool> cancelled, out string error)
    {
        error = null;
        if (r == null || !r.CanInstall) { error = "sürümde paket veya SHA-256 dosyası yok"; return null; }
        if (!UpdateLogic.IsAllowedAssetUrl(r.ZipUrl, r.Tag, UpdateLogic.ZipAsset) || !UpdateLogic.IsAllowedAssetUrl(r.ShaUrl, r.Tag, UpdateLogic.ShaAsset)) { error = "beklenmeyen indirme adresi"; return null; }
        if (report == null) report = delegate { };
        if (cancelled == null) cancelled = () => false;
        var work = Path.Combine(WorkRoot, r.Version + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            report(UpdateStage.Downloading, 0, 0);
            var shaText = Encoding.ASCII.GetString(Get(r.ShaUrl, MaxShaBytes, null));
            if (cancelled()) { TryDelete(work); error = CancelledText; return null; }
            var zip = Path.Combine(work, UpdateLogic.ZipAsset);
            if (!DownloadZip(r.ZipUrl, zip, report, cancelled)) { TryDelete(work); error = CancelledText; return null; }
            report(UpdateStage.Verifying, 0, 0);
            if (cancelled()) { TryDelete(work); error = CancelledText; return null; }
            report(UpdateStage.Preparing, 0, 0);
            var pkg = VerifyAndExtract(zip, shaText, work, out error);
            if (pkg == null) TryDelete(work);
            return pkg;
        }
        catch (Exception ex) { TryDelete(work); error = Describe(ex); return null; }
    }

    // Streams the zip to disk. WebException / IOException are retried (each attempt restarts the file),
    // except an HTTP 4xx answer. Returns false when cancelled; throws when all attempts failed.
    static bool DownloadZip(string url, string zipPath, Action<UpdateStage, long, long> report, Func<bool> cancelled)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (cancelled()) return false;
            try
            {
                using (var resp = Open(url, null))
                {
                    if (resp.ContentLength > MaxZipBytes) throw new InvalidOperationException("dosya beklenenden büyük");
                    long total = resp.ContentLength > 0 ? resp.ContentLength : 0;
                    report(UpdateStage.Downloading, 0, total);
                    using (var s = resp.GetResponseStream()) using (var fs = File.Create(zipPath))
                        return CopyWithProgress(s, fs, MaxZipBytes, delegate(long done) { report(UpdateStage.Downloading, done, total); }, cancelled);
                }
            }
            catch (Exception ex)
            {
                var web = ex as WebException;
                if (web == null && !(ex is IOException)) throw;
                var http = web != null ? web.Response as HttpWebResponse : null;
                if (http != null && (int)http.StatusCode >= 400 && (int)http.StatusCode < 500) throw;
                if (attempt >= RetryWaitsMs.Length) throw;
                if (!WaitCancellable(RetryWaitsMs[attempt], cancelled)) return false;
            }
        }
    }

    // Sleeps in short slices so a cancel is noticed quickly. False when cancelled.
    static bool WaitCancellable(int ms, Func<bool> cancelled)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cancelled()) return false;
            System.Threading.Thread.Sleep(50);
        }
        return !cancelled();
    }

    // Copies source to target in 64 KB chunks. progress gets the cumulative byte count after each chunk
    // (may be null). Returns false when cancelled (checked between chunks); throws past maxBytes.
    public static bool CopyWithProgress(Stream source, Stream target, long maxBytes, Action<long> progress, Func<bool> cancelled)
    {
        var buf = new byte[64 * 1024]; long done = 0; int n;
        while ((n = source.Read(buf, 0, buf.Length)) > 0)
        {
            done += n;
            if (done > maxBytes) throw new InvalidOperationException("paket çok büyük");
            target.Write(buf, 0, n);
            if (progress != null) progress(done);
            if (cancelled != null && cancelled()) return false;
        }
        return true;
    }
    // SHA-256 of the zip must equal the hash in shaText (mandatory). Only then is the zip extracted
    // to <workDir>\paket. A mismatch or a missing hash deletes the zip and extracts nothing.
    public static string VerifyAndExtract(string zipPath, string shaText, string workDir, out string error)
    {
        error = null;
        var expected = UpdateLogic.ParseSha256(shaText);
        if (expected == null) { TryDeleteFile(zipPath); error = "SHA-256 özeti bulunamadı; güncelleme durduruldu"; return null; }
        string actual;
        using (var sha = SHA256.Create()) using (var fs = File.OpenRead(zipPath))
            actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) { TryDeleteFile(zipPath); error = "SHA-256 doğrulanamadı; güncelleme durduruldu"; return null; }
        var pkg = Path.Combine(workDir, "paket");
        if (Directory.Exists(pkg)) Directory.Delete(pkg, true);
        // .NET 4.6.2+ refuses entries that would land outside pkg ("..\" paths, zip slip): the whole
        // package is then rejected and whatever was extracted is removed.
        try { ZipFile.ExtractToDirectory(zipPath, pkg); }
        catch (Exception ex) { TryDelete(pkg); TryDeleteFile(zipPath); error = "paket açılamadı: " + ex.Message; return null; }
        if (!File.Exists(Path.Combine(pkg, "kur.ps1")) || !File.Exists(Path.Combine(pkg, "uygulama", UpdateLogic.ExeName)))
        {
            TryDelete(pkg); error = "paket beklenmeyen biçimde"; return null;
        }
        return pkg;
    }

    // Install mechanics: a running exe cannot overwrite itself. Of the two options (rename the
    // running exe to .old, or hand over to a helper), the helper is the safer one: the app starts
    // the verified package's kur.ps1 -Update in a hidden PowerShell and exits. kur.ps1 waits for
    // this process to end, backs up every file it replaces, copies the new files, restores the
    // backup if any copy fails (the old exe stays), and starts the app again. Settings, pins,
    // history (%APPDATA%) and the profile pack in the install dir are not touched.
    public const string HelperReadyFile = ".bekliyor";

    public static bool StartInstall(string packageDir, string installDir, string version, int waitPid, bool background, out string error)
    {
        error = null;
        try
        {
            var kur = Path.Combine(packageDir, "kur.ps1");
            if (!File.Exists(kur)) { error = "kur.ps1 bulunamadı"; return false; }
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(kur) +
                       " -Update -InstallDir " + Quote(installDir.TrimEnd('\\')) + " -WaitPid " + waitPid.ToString(CultureInfo.InvariantCulture) + (background ? " -Background" : "") +
                       (UpdateLogic.NormalizeTag(version) != null ? " -Version " + UpdateLogic.NormalizeTag(version) : "");
            var psi = new ProcessStartInfo(ps, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = packageDir };
            using (var helper = Process.Start(psi))
            {
                // The app exits right after this returns. If PowerShell is blocked or dies at once,
                // stay up and report it instead of leaving the user without the app. kur.ps1 writes
                // <package>\.bekliyor once its checks passed and it is waiting for this process; give
                // it up to 10 s (PowerShell can take a few seconds to start).
                if (helper == null) { error = "güncelleme yardımcısı başlatılamadı"; return false; }
                var ready = Path.Combine(packageDir, HelperReadyFile);
                var sw = Stopwatch.StartNew();
                while (!File.Exists(ready))
                {
                    if (helper.WaitForExit(200)) { error = "güncelleme yardımcısı hemen kapandı (kod " + helper.ExitCode + ")"; return false; }
                    if (sw.ElapsedMilliseconds > 10000) break;   // slow start, still running: it will wait for this process
                }
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }

    // Read-only GET of a GitHub API JSON document (diagnostics duplicate check). Never call on the
    // UI thread. Throws on network / size problems.
    public static string GetText(string url)
    {
        if (url == null || !url.StartsWith("https://api.github.com/", StringComparison.Ordinal)) throw new InvalidOperationException("beklenmeyen adres");
        return Encoding.UTF8.GetString(Get(url, MaxJsonBytes, "application/vnd.github+json"));
    }

    // Opens the GET response (caller disposes). HTTPS only, including the final redirect hop.
    static HttpWebResponse Open(string url, string accept)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("yalnızca HTTPS");
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET"; req.Timeout = TimeoutMs; req.ReadWriteTimeout = TimeoutMs;
        req.UserAgent = UpdateLogic.UserAgent; req.AllowAutoRedirect = true; req.MaximumAutomaticRedirections = 5;
        req.UseDefaultCredentials = false; req.CookieContainer = null;
        if (accept != null) req.Accept = accept;
        var resp = (HttpWebResponse)req.GetResponse();
        // GitHub serves assets from its download host; the final hop must still be HTTPS.
        if (resp.ResponseUri == null || resp.ResponseUri.Scheme != Uri.UriSchemeHttps) { resp.Close(); throw new InvalidOperationException("yalnızca HTTPS"); }
        return resp;
    }

    static byte[] Get(string url, long maxBytes, string accept)
    {
        using (var resp = Open(url, accept))
        {
            if (resp.ContentLength > maxBytes) throw new InvalidOperationException("dosya beklenenden büyük");
            using (var s = resp.GetResponseStream()) using (var ms = new MemoryStream())
            {
                var buf = new byte[81920]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > maxBytes) throw new InvalidOperationException("dosya beklenenden büyük");
                }
                return ms.ToArray();
            }
        }
    }
    static string Describe(Exception ex)
    {
        var w = ex as WebException;
        if (w != null)
        {
            var http = w.Response as HttpWebResponse;
            if (http != null) return "sunucu yanıtı " + (int)http.StatusCode;
            if (w.Status == WebExceptionStatus.Timeout) return "zaman aşımı";
            return "bağlantı kurulamadı";
        }
        return ex.Message;
    }

    static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    static void TryDeleteFile(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    // Contents of the install dir's failure marker, or null.
    public static string ReadFailureMarker(string installDir)
    {
        try { var f = Path.Combine(installDir, UpdateLogic.FailureMarkerName); return File.Exists(f) ? File.ReadAllText(f) : null; }
        catch { return null; }
    }

    // kur.ps1 -Update writes this file into the install dir after a successful update (see ParseUpdatedMarker).
    public const string UpdatedMarkerName = "guncellendi.txt";

    // Reads and deletes the success marker: the new version when it is valid, matches the running version
    // and is less than 24 h old; otherwise null (a stale or foreign-version marker is deleted silently).
    public static string TakeUpdatedMarker(string installDir)
    {
        try
        {
            var f = Path.Combine(installDir, UpdatedMarkerName);
            if (!File.Exists(f)) return null;
            var text = File.ReadAllText(f);
            TryDeleteFile(f);
            string version;
            if (!UpdateLogic.ParseUpdatedMarker(text, out version)) return null;
            var lines = text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var when = DateTime.Parse(lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var age = DateTime.UtcNow - when;
            if (age >= TimeSpan.FromHours(24) || age <= -TimeSpan.FromHours(24)) return null;
            return UpdateLogic.CompareVersions(version, UpdateLogic.CurrentVersion) == 0 ? version : null;
        }
        catch { return null; }
    }

    // Opens a page of this repository's releases in the browser (other addresses are ignored).
    public static void OpenReleasePage(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(UpdateLogic.ReleasesPage, StringComparison.Ordinal)) url = UpdateLogic.ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

// "ask" mode notice: a small glass card in the corner of the screen, above other windows, that does
// not take focus. Same material as GlassDialog. Offer state: update now / later / skip this version,
// plus the release notes. After "update now" the card stays open and shows progress (download,
// verify, prepare, restart), a waiting reason, or a failure with Retry / open release page.
internal sealed class UpdateNotice : Form
{
    static int S(float v) { return Ui.S(v); }
    const int WS_EX_TOOLWINDOW = 0x80;
    string caption, body, stageLine;
    Rectangle textRect, barRect;
    UpdateStage stage = UpdateStage.Offer;
    long done, total;
    readonly string version, offerCaption, offerBody;
    readonly bool canInstall;
    readonly SButton primary, later, skip, notesBtn, cancelBtn, retryBtn, openBtn, closeBtn, waitLaterBtn;
    Timer anim;
    public event Action UpdateNow, Later, Skip, Notes, Cancel, Retry;

    // installedCopy: running from the install dir. Otherwise, or without the package / .sha256 asset,
    // the primary button opens the release page instead of installing.
    public UpdateNotice(ReleaseInfo r, string current, bool installedCopy)
    {
        canInstall = installedCopy && r.CanInstall;
        version = r.Version;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
        BackColor = Ui.GlassPrimary; // the buttons paint their parent's color behind them: same as the glass card
        ForeColor = Ui.Text; Font = SFonts.Nav;
        Text = "Güncelleme";
        offerCaption = "Sürüm " + r.Version + " hazır";
        var notes = UpdateLogic.NotesPreview(r.Notes, 160);
        offerBody = (canInstall
            ? "Yüklü sürüm " + current + ". Şimdi güncelleyebilir ya da sonraya bırakabilirsiniz."
            : installedCopy ? "Yüklü sürüm " + current + ". Bu sürüm uygulama içinden kurulamıyor; sürüm sayfasından indirin."
            : "Yüklü sürüm " + current + ". Bu kopya kurulum klasöründe değil; yeni sürümü sürüm sayfasından indirin.")
            + (notes.Length > 0 ? "\n" + notes : "");

        primary = new SButton(canInstall ? "Şimdi güncelle" : "Sürüm sayfasını aç", SButtonKind.Primary);
        later = new SButton("Sonra", SButtonKind.Secondary);
        skip = new SButton("Bu sürümü atla", SButtonKind.Ghost);
        notesBtn = new SButton("Sürüm notları", SButtonKind.Ghost, Glyph.Open);
        cancelBtn = new SButton("İptal", SButtonKind.Secondary);
        retryBtn = new SButton("Yeniden dene", SButtonKind.Primary);
        openBtn = new SButton("Sürüm sayfasını aç", SButtonKind.Secondary);
        closeBtn = new SButton("Kapat", SButtonKind.Secondary);          // Failed: dismiss the card
        waitLaterBtn = new SButton("Sonra", SButtonKind.Secondary);      // Waiting: hide the card; the pending install still proceeds
        primary.Click += delegate
        {
            if (canInstall)
            {
                SetStage(UpdateStage.Downloading, 0, 0, version); // working look right away; the caller drives the rest
                if (UpdateNow != null) UpdateNow();
            }
            else { Close(); if (Notes != null) Notes(); }
        };
        later.Click += delegate { Close(); if (Later != null) Later(); };
        skip.Click += delegate { Close(); if (Skip != null) Skip(); };
        notesBtn.Click += delegate { if (Notes != null) Notes(); };
        cancelBtn.Click += delegate { if (Cancel != null) Cancel(); };
        retryBtn.Click += delegate
        {
            SetStage(UpdateStage.Downloading, 0, 0, version);
            if (Retry != null) Retry();
        };
        openBtn.Click += delegate { if (Notes != null) Notes(); };
        closeBtn.Click += delegate { Close(); };
        waitLaterBtn.Click += delegate { Close(); };
        Controls.AddRange(new Control[] { primary, later, skip, notesBtn, cancelBtn, retryBtn, openBtn, closeBtn, waitLaterBtn });

        caption = offerCaption; body = offerBody;
        ApplyLayout();
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - S(16), area.Bottom - Height - S(16));
    }

    public void SetStage(UpdateStage newStage, long doneBytes, long totalBytes, string ver)
    {
        try
        {
            if (string.IsNullOrEmpty(ver)) ver = version;
            if (newStage == UpdateStage.Waiting) { SetWaiting(UpdateLogic.StageText(newStage, doneBytes, totalBytes, ver)); return; }
            if (newStage == UpdateStage.Failed) { SetFailed(UpdateLogic.StageText(newStage, doneBytes, totalBytes, ver)); return; }
            stage = newStage; done = doneBytes; total = totalBytes;
            if (newStage == UpdateStage.Offer) { caption = offerCaption; body = offerBody; stageLine = null; }
            else
            {
                caption = "Sürüm " + ver + " güncelleniyor"; body = null;
                stageLine = UpdateLogic.StageText(newStage, doneBytes, totalBytes, ver);
            }
            ApplyLayout();
        }
        catch { }
    }

    public void SetWaiting(string reason)
    {
        try
        {
            stage = UpdateStage.Waiting; done = 0; total = 0;
            caption = "Güncelleme bekliyor"; body = reason ?? ""; stageLine = null;
            ApplyLayout();
        }
        catch { }
    }

    public void SetFailed(string reason)
    {
        try
        {
            stage = UpdateStage.Failed; done = 0; total = 0;
            caption = "Güncelleme başarısız"; body = reason ?? ""; stageLine = null;
            ApplyLayout();
        }
        catch { }
    }

    bool HasBar { get { return stage != UpdateStage.Offer && stage != UpdateStage.Failed; } }
    bool Determinate { get { return stage == UpdateStage.Downloading && UpdateLogic.Percent(done, total) >= 0; } }

    // Resizes the card for the current state (same width, bottom edge stays put) and places the controls.
    void ApplyLayout()
    {
        var width = S(440); var textW = width - S(96);
        var bottom = Bottom;
        var showText = body != null;
        var textH = 0;
        if (showText)
            textH = Math.Min(S(120), Math.Max(S(20), TextRenderer.MeasureText(body, SFonts.RowDesc, new Size(textW, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height));
        var y = S(56);
        if (stageLine != null) y += S(24);
        textRect = showText ? new Rectangle(S(76), S(56), textW, textH) : Rectangle.Empty;
        if (showText) y = textRect.Bottom;
        var offer = stage == UpdateStage.Offer;
        var failed = stage == UpdateStage.Failed;
        barRect = Rectangle.Empty;
        int h;
        if (HasBar)
        {
            y += showText ? S(14) : S(8);
            barRect = new Rectangle(S(76), y, textW, S(8));
            y = barRect.Bottom;
            h = (stage == UpdateStage.Downloading || stage == UpdateStage.Waiting) ? y + S(16) + cancelBtn.Height + S(22) : y + S(34);
        }
        else h = offer ? textRect.Bottom + S(104) : textRect.Bottom + S(24) + retryBtn.Height + S(22);
        Size = new Size(width, h);
        Top = bottom - Height;

        var by = Height - S(22) - primary.Height;
        primary.Visible = later.Visible = skip.Visible = notesBtn.Visible = offer;
        cancelBtn.Visible = stage == UpdateStage.Downloading;
        waitLaterBtn.Visible = stage == UpdateStage.Waiting;
        retryBtn.Visible = openBtn.Visible = closeBtn.Visible = failed;
        primary.Location = new Point(Width - S(22) - primary.Width, by);
        later.Location = new Point(primary.Left - S(8) - later.Width, by);
        skip.Location = new Point(S(22), by);
        notesBtn.Location = new Point(S(68), textRect.Bottom + S(6));
        cancelBtn.Location = new Point(Width - S(22) - cancelBtn.Width, Height - S(22) - cancelBtn.Height);
        retryBtn.Location = new Point(Width - S(22) - retryBtn.Width, Height - S(22) - retryBtn.Height);
        openBtn.Location = new Point(retryBtn.Left - S(8) - openBtn.Width, retryBtn.Top);
        closeBtn.Location = new Point(openBtn.Left - S(8) - closeBtn.Width, retryBtn.Top);
        waitLaterBtn.Location = new Point(Width - S(22) - waitLaterBtn.Width, Height - S(22) - waitLaterBtn.Height);
        UpdateTimer();
        Invalidate();
    }

    // The sliding segment needs a timer, but only while a bar without a known percent is on screen.
    void UpdateTimer()
    {
        var need = HasBar && !Determinate && Visible && !IsDisposed;
        if (need && anim == null)
        {
            anim = new Timer(); anim.Interval = 30;
            anim.Tick += delegate { if (!barRect.IsEmpty) Invalidate(Rectangle.Inflate(barRect, S(2), S(2))); };
            anim.Start();
        }
        else if (!need && anim != null) StopTimer();
    }

    void StopTimer()
    {
        if (anim == null) return;
        try { anim.Stop(); anim.Dispose(); } catch { }
        anim = null;
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { StopTimer(); base.OnFormClosed(e); }
    protected override void Dispose(bool disposing) { if (disposing) StopTimer(); base.Dispose(disposing); }

    // Esc closes the card where closing is safe (Failed, Waiting); a running download keeps its Cancel button.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && (stage == UpdateStage.Failed || stage == UpdateStage.Waiting)) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW; return cp; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Backdrop.SetCorner(Handle, 2); var d = Themes.IsLight ? 0 : 1; NativeMethods.DwmSetWindowAttribute(Handle, 20, ref d, 4); } catch { }
    }

    void PaintBar(Graphics g)
    {
        var track = new RectangleF(barRect.X, barRect.Y, barRect.Width, barRect.Height);
        var r = track.Height / 2F;
        Ui.FillRound(g, Color.FromArgb(48, Ui.Text), track, r);
        Glass.Paint(g, track, r, GlassKind.Secondary, 1F);
        RectangleF fill;
        var pct = UpdateLogic.Percent(done, total);
        if (Determinate)
            fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * pct / 100F), track.Height);
        else
        {
            var segW = track.Width * 0.3F;
            var period = 1600;
            var t = (Environment.TickCount & 0x7FFFFFFF) % period / (float)period;
            fill = new RectangleF(track.X - segW + t * (track.Width + segW), track.Y, segW, track.Height);
        }
        var state = g.Save();
        using (var clip = Ui.Round(track, r)) g.SetClip(clip, CombineMode.Intersect);
        Ui.FillRound(g, Ui.Highlight, fill, r);
        g.Restore(state);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var panel = new RectangleF(S(8), S(8), Width - S(16), Height - S(16));
        Ui.FillRound(g, Ui.GlassPrimary, panel, S(14));
        Glass.Paint(g, panel, S(14), GlassKind.Primary, 1F);
        var chip = new RectangleF(S(26), S(26), S(36), S(36));
        Glass.Paint(g, chip, S(11), GlassKind.Secondary, 1.8F);
        Assets.Draw(g, "action-update", RectangleF.Inflate(chip, -S(8), -S(8)), stage == UpdateStage.Failed ? Ui.Muted : Ui.Highlight, 1.1F); // download arrow (vector asset, sharp at any size)
        TextRenderer.DrawText(g, caption, SFonts.Big, new Point(S(76), S(26)), Ui.Text, Ui.Flags);
        if (stageLine != null)
            TextRenderer.DrawText(g, stageLine, SFonts.RowDesc, new Rectangle(S(76), S(56), Width - S(96), S(22)), Ui.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (body != null)
            TextRenderer.DrawText(g, body, SFonts.RowDesc, textRect, Ui.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (!barRect.IsEmpty) PaintBar(g);
        Glass.WindowFrame(g, ClientRectangle, S(8));
    }
}
