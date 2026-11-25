using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// =====================================================================
// Sending error reports to GitHub Issues with an access token embedded at build time.
//   IssueToken        - the build-time resource "issue.token" (obfuscated) -> token or null.
//   IssueLimits       - per-install limits (3 a day, 1 per fingerprint a day, 24 h stop after 401). Pure.
//   GitHubIssueClient - create issue / comment on a duplicate. Pure decisions; the HTTP transport is
//                       injected (tests use fakes, the app uses HttpTransport).
// Anyone can extract the token from the exe, so what it can do matters (docs\RELEASING.md):
//  - recommended: a classic token with only "public_repo" from a separate bot account that owns no
//    repository and is not a collaborator: on this public repository it can open issues and write
//    comments as a stranger (worst case: spam under the bot's name; block the account, revoke).
//  - alternative: the owner's fine-grained token with "Issues: Read and write" on this repository:
//    it can also edit, close, lock and relabel ANY issue and edit / delete comments, all shown as
//    the maintainer.
// The token never goes to the log, a report, a status text, a test or the repository. Without the
// resource the app keeps the browser flow.
// =====================================================================

internal static class IssueToken
{
    public const string ResourceName = "issue.token";

    // Fixed XOR key. This only keeps the token out of a plain string scan of the exe (and out of
    // naive secret scanners); it is NOT protection: the key is right here. build.ps1 calls Encode
    // (it loads the freshly built exe), so there is one implementation.
    static readonly byte[] Key = Encoding.ASCII.GetBytes("VpnManager/issue-token/v1");
    static readonly Regex Shape = new Regex(@"^(?:github_pat_[A-Za-z0-9_]{20,255}|gh[pousr]_[A-Za-z0-9]{20,255})$", RegexOptions.CultureInvariant);

    public static bool LooksLikeToken(string t) { return t != null && Shape.IsMatch(t); }

    // token -> resource bytes (ASCII base64 of the XORed UTF-8 bytes).
    public static byte[] Encode(string token)
    {
        if (!LooksLikeToken(token)) throw new ArgumentException("not a GitHub token");
        var b = Encoding.UTF8.GetBytes(token);
        for (var i = 0; i < b.Length; i++) b[i] ^= Key[i % Key.Length];
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(b));
    }

    // Resource bytes -> token, or null for anything that does not decode to a token.
    public static string Decode(byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > 4096) return null;
        try
        {
            var b = Convert.FromBase64String(Encoding.ASCII.GetString(data).Trim());
            for (var i = 0; i < b.Length; i++) b[i] ^= Key[i % Key.Length];
            var t = new UTF8Encoding(false, true).GetString(b);
            return LooksLikeToken(t) ? t : null;
        }
        catch { return null; }
    }

    static string token; static bool loaded;
    static readonly object gate = new object();

    // The embedded token (read once), or null when this build has none.
    public static string Value
    {
        get
        {
            lock (gate)
            {
                if (loaded) return token;
                loaded = true;
                try
                {
                    using (var s = typeof(IssueToken).Assembly.GetManifestResourceStream(ResourceName))
                    {
                        if (s == null) return null;
                        using (var ms = new MemoryStream()) { s.CopyTo(ms); token = Decode(ms.ToArray()); }
                    }
                }
                catch { token = null; }
                return token;
            }
        }
    }

    public static bool Available { get { return Value != null; } }
}

// Limits of one installation, persisted in %APPDATA%\OpenVpnQuickSearch\issue-limits.json.
internal sealed class IssueLimitState
{
    public List<DateTime> Sent = new List<DateTime>();                                         // UTC, last 24 h
    public Dictionary<string, DateTime> ByFingerprint = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    public DateTime StoppedUntilUtc = DateTime.MinValue;                                      // after a 401
}

internal static class IssueLimits
{
    public const int PerDay = 3;
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public const string Manual = "manual";   // key of reports without a fingerprint

    public enum Verdict { Allowed, Stopped, DailyLimit, FingerprintLimit }

    static string Key(string fingerprint) { return string.IsNullOrEmpty(fingerprint) ? Manual : fingerprint; }

    public static Verdict Check(IssueLimitState s, string fingerprint, DateTime nowUtc)
    {
        if (s == null) return Verdict.Allowed;
        if (nowUtc < s.StoppedUntilUtc && s.StoppedUntilUtc - nowUtc <= Window) return Verdict.Stopped;
        DateTime last;
        if (s.ByFingerprint.TryGetValue(Key(fingerprint), out last) && nowUtc - last < Window && last <= nowUtc) return Verdict.FingerprintLimit;
        if (s.Sent.Count(t => nowUtc - t < Window && t <= nowUtc) >= PerDay) return Verdict.DailyLimit;
        return Verdict.Allowed;
    }

    public static void Record(IssueLimitState s, string fingerprint, DateTime nowUtc)
    {
        s.Sent.RemoveAll(t => nowUtc - t >= Window || t > nowUtc);
        s.Sent.Add(nowUtc);
        foreach (var k in s.ByFingerprint.Where(kv => nowUtc - kv.Value >= Window).Select(kv => kv.Key).ToList()) s.ByFingerprint.Remove(k);
        s.ByFingerprint[Key(fingerprint)] = nowUtc;
    }

    // 401: the token is expired or revoked; no further attempt for 24 h.
    public static void Stop(IssueLimitState s, DateTime nowUtc) { s.StoppedUntilUtc = nowUtc + Window; }

    public static string ToJson(IssueLimitState s)
    {
        var d = new Dictionary<string, object>();
        d["sent"] = s.Sent.Select(t => t.Ticks).ToArray();
        d["byFingerprint"] = s.ByFingerprint.ToDictionary(kv => kv.Key, kv => (object)kv.Value.Ticks);
        d["stoppedUntil"] = s.StoppedUntilUtc.Ticks;
        return new JavaScriptSerializer().Serialize(d);
    }

    // Broken or missing file: a fresh state (the limits start over, nothing else).
    public static IssueLimitState FromJson(string json)
    {
        var s = new IssueLimitState();
        if (string.IsNullOrEmpty(json)) return s;
        try
        {
            var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) return s;
            object v;
            if (d.TryGetValue("sent", out v) && v is object[]) foreach (var o in (object[])v) { var t = Ticks(o); if (t != DateTime.MinValue) s.Sent.Add(t); }
            if (d.TryGetValue("byFingerprint", out v) && v is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)v) { var t = Ticks(kv.Value); if (t != DateTime.MinValue && kv.Key.Length <= 16) s.ByFingerprint[kv.Key] = t; }
            if (d.TryGetValue("stoppedUntil", out v)) s.StoppedUntilUtc = Ticks(v);
        }
        catch { return new IssueLimitState(); }
        return s;
    }

    static DateTime Ticks(object o)
    {
        try
        {
            var n = Convert.ToInt64(o, CultureInfo.InvariantCulture);
            return n > 0 && n <= DateTime.MaxValue.Ticks ? new DateTime(n, DateTimeKind.Utc) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }
}

// One HTTP call as the client wants it; the transport performs it. Status 0 = no answer (network).
internal sealed class ApiRequest
{
    public string Method, Url, Json;
    public bool Authorized;           // send the embedded token (never for the public search)
    public string Token;              // set only when Authorized; never logged or printed
    public override string ToString() { return Method + " " + Url; }   // no token, no body
}

internal sealed class ApiResponse { public int Status; public string Body = ""; }

internal enum SendOutcome { Created, Commented, Stopped, DailyLimit, FingerprintLimit, Unauthorized, Forbidden, Failed, NoToken }

internal sealed class SendResult
{
    public SendOutcome Outcome; public int Number; public string Url; public int Status;
    public bool Sent { get { return Outcome == SendOutcome.Created || Outcome == SendOutcome.Commented; } }
}

internal static class GitHubIssueClient
{
    public static readonly string ApiBase = "https://api.github.com/repos/" + UpdateLogic.Repo + "/issues";
    public const string ApiVersion = "2022-11-28";
    public const int MaxBodyChars = 60 * 1024;
    public const int MaxTitleChars = 200;
    public const int TimeoutMs = 15000;

    public static string IssueJson(string title, string body, bool withLabel)
    {
        var d = new Dictionary<string, object>();
        title = title ?? "";
        d["title"] = CutTitle(title);
        d["body"] = body ?? "";
        if (withLabel) d["labels"] = new[] { IssueLink.Label };
        return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(d);
    }

    // At most MaxTitleChars; cut in the middle so a trailing " (<fingerprint>)" survives.
    public static string CutTitle(string title)
    {
        title = title ?? "";
        if (title.Length <= MaxTitleChars) return title;
        var tail = "";
        var open = title.LastIndexOf(" (", StringComparison.Ordinal);
        if (title.EndsWith(")", StringComparison.Ordinal) && open > 0 && title.Length - open <= 40) tail = title.Substring(open);
        return title.Substring(0, MaxTitleChars - tail.Length - 1) + "…" + tail;
    }

    public static string CommentJson(string body)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "body", body ?? "" } });
    }

    public static string RepeatComment(ReportEnvironment env)
    {
        return "Tekrar: sürüm " + (env != null ? env.AppVersion : "?") + ", Windows " + (env != null ? env.Windows : "?");
    }

    public static string Truncate(string body)
    {
        if (body == null) return "";
        if (body.Length <= MaxBodyChars) return body;
        var cut = body.Substring(0, MaxBodyChars - 200);
        if (Regex.Matches(cut, "```").Count % 2 == 1) cut += "\n```";
        return cut + "\n\n_(Rapor 60 KB sınırında kısaltıldı.)_\n";
    }

    // 422 that names the label ("hata" missing in the repo, or no right to set it): retry without labels.
    public static bool IsLabelError(ApiResponse r)
    {
        return r != null && r.Status == 422 && r.Body != null && r.Body.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // number + html_url of a created issue / comment (comments: number = the issue's).
    public static IssueRef ParseCreated(string json)
    {
        try
        {
            var d = new JavaScriptSerializer().DeserializeObject(json ?? "") as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            var url = d.TryGetValue("html_url", out v) ? v as string : null;
            if (!IssueLink.IsOurIssueUrl(url)) return null;
            var n = 0;
            if (d.TryGetValue("number", out v) && v != null) n = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return new IssueRef { Number = n, Url = url, State = "open" };
        }
        catch { return null; }
    }

    // URLs the transport may call: this repository's issues API and the public search, https only.
    public static bool IsAllowedUrl(string url)
    {
        if (url == null || url.IndexOfAny(new[] { ' ', '\r', '\n', '"' }) >= 0) return false;
        if (url.StartsWith("https://api.github.com/search/issues?", StringComparison.Ordinal)) return true;
        return url == ApiBase || Regex.IsMatch(url, "^" + Regex.Escape(ApiBase) + @"/\d{1,9}/comments$");
    }

    public static bool CommentFallsThrough(ApiResponse r)
    {
        if (r == null) return false;
        if (r.Status == 404 || r.Status == 410) return true;
        return r.Status == 403 && r.Body != null && r.Body.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // The token goes only to the create-issue URL and this repository's comment URLs (never the search).
    public static bool MayAuthorize(string url)
    {
        return url != null && (url == ApiBase || Regex.IsMatch(url, "^" + Regex.Escape(ApiBase) + @"/\d{1,9}/comments$"));
    }

    static SendOutcome FromStatus(int status)
    {
        return status == 401 ? SendOutcome.Unauthorized : status == 403 || status == 429 ? SendOutcome.Forbidden : SendOutcome.Failed;
    }

    // Creates the issue, or comments once on an open duplicate. title / body must already be sanitized.
    // limits is updated in place (caller persists it). Never throws.
    public static SendResult Send(string title, string body, string fingerprint, ReportEnvironment env, string token,
                                  Func<ApiRequest, ApiResponse> transport, IssueLimitState limits, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token) || transport == null) return new SendResult { Outcome = SendOutcome.NoToken };
        if (limits == null) limits = new IssueLimitState();
        switch (IssueLimits.Check(limits, fingerprint, nowUtc))
        {
            case IssueLimits.Verdict.Stopped: return new SendResult { Outcome = SendOutcome.Stopped };
            case IssueLimits.Verdict.DailyLimit: return new SendResult { Outcome = SendOutcome.DailyLimit };
            case IssueLimits.Verdict.FingerprintLimit: return new SendResult { Outcome = SendOutcome.FingerprintLimit };
        }
        try
        {
            body = Truncate(body);
            IssueRef dup = null;
            if (IssueLink.IsValidFingerprint(fingerprint))
            {
                var search = Call(transport, new ApiRequest { Method = "GET", Url = IssueLink.SearchUrl(fingerprint) });
                if (search.Status == 200) dup = IssueLink.FindDuplicate(search.Body, fingerprint);
            }
            ApiResponse resp;
            if (dup != null && dup.Open && !dup.Locked && dup.Number > 0)
            {
                resp = Call(transport, new ApiRequest { Method = "POST", Url = ApiBase + "/" + dup.Number.ToString(CultureInfo.InvariantCulture) + "/comments", Json = CommentJson(RepeatComment(env)), Authorized = true, Token = token });
                if (resp.Status == 201)
                {
                    IssueLimits.Record(limits, fingerprint, nowUtc);
                    var c = ParseCreated(resp.Body);
                    return new SendResult { Outcome = SendOutcome.Commented, Number = dup.Number, Url = c != null ? c.Url : dup.Url, Status = 201 };
                }
                if (resp.Status == 401) { IssueLimits.Stop(limits, nowUtc); return new SendResult { Outcome = SendOutcome.Unauthorized, Status = 401 }; }
                // Only "issue gone" (404 / 410) or "locked" (403 that says so) lead to a new issue that
                // points at the old one; any other 403 / 429 is a refusal or rate limit (browser flow).
                if (!CommentFallsThrough(resp)) return new SendResult { Outcome = FromStatus(resp.Status), Status = resp.Status };
            }
            if (dup != null && dup.Number > 0)
                body = "Önceki kayıt: #" + dup.Number.ToString(CultureInfo.InvariantCulture) + " (" + (dup.Open ? "açık, kilitli" : "kapalı") + ")\n\n" + body;
            resp = Call(transport, new ApiRequest { Method = "POST", Url = ApiBase, Json = IssueJson(title, body, true), Authorized = true, Token = token });
            if (IsLabelError(resp))
                resp = Call(transport, new ApiRequest { Method = "POST", Url = ApiBase, Json = IssueJson(title, body, false), Authorized = true, Token = token });
            if (resp.Status == 201)
            {
                var created = ParseCreated(resp.Body);
                if (created == null) return new SendResult { Outcome = SendOutcome.Failed, Status = 201 };
                IssueLimits.Record(limits, fingerprint, nowUtc);
                return new SendResult { Outcome = SendOutcome.Created, Number = created.Number, Url = created.Url, Status = 201 };
            }
            if (resp.Status == 401) IssueLimits.Stop(limits, nowUtc);
            return new SendResult { Outcome = FromStatus(resp.Status), Status = resp.Status };
        }
        catch { return new SendResult { Outcome = SendOutcome.Failed }; }
    }

    static ApiResponse Call(Func<ApiRequest, ApiResponse> transport, ApiRequest req)
    {
        if (!IsAllowedUrl(req.Url)) return new ApiResponse { Status = 0 };
        if (!MayAuthorize(req.Url)) { req.Authorized = false; req.Token = null; }
        try { return transport(req) ?? new ApiResponse(); }
        catch { return new ApiResponse(); }
    }

    // The real transport: https only, this repository only, no redirects (the token must not follow
    // one), TLS 1.2, 15 s timeout. Never call on the UI thread.
    public static ApiResponse HttpTransport(ApiRequest req)
    {
        if (req == null || !IsAllowedUrl(req.Url)) return new ApiResponse();
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = (HttpWebRequest)WebRequest.Create(req.Url);
            http.Method = req.Method; http.Timeout = TimeoutMs; http.ReadWriteTimeout = TimeoutMs;
            http.UserAgent = UpdateLogic.UserAgent; http.AllowAutoRedirect = false;
            http.UseDefaultCredentials = false; http.CookieContainer = null;
            http.Accept = "application/vnd.github+json";
            http.Headers["X-GitHub-Api-Version"] = ApiVersion;
            if (req.Authorized && !string.IsNullOrEmpty(req.Token) && MayAuthorize(req.Url)) http.Headers[HttpRequestHeader.Authorization] = "Bearer " + req.Token;
            if (req.Json != null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(req.Json);
                http.ContentType = "application/json; charset=utf-8";
                http.ContentLength = bytes.Length;
                using (var s = http.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = (HttpWebResponse)http.GetResponse()) return Read(resp);
        }
        catch (WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp == null) return new ApiResponse();
            using (resp) return Read(resp);
        }
        catch { return new ApiResponse(); }
    }

    static ApiResponse Read(HttpWebResponse resp)
    {
        var r = new ApiResponse { Status = (int)resp.StatusCode };
        try
        {
            using (var s = resp.GetResponseStream()) using (var ms = new MemoryStream())
            {
                var buf = new byte[16384]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0 && ms.Length < 1024 * 1024) ms.Write(buf, 0, n);
                r.Body = Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch { }
        return r;
    }
}
