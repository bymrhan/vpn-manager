# Diagnostics: app log and consent-based GitHub error reports (report)

Branch `feat/diagnostics` (from `main` at `883082d`). Request: the app keeps its own log, critical
errors from users are collected on GitHub, and fixes are driven from there.

## Design as built

**No silent upload.** A public app cannot hold a GitHub write token, so the app never sends a
report. It prepares a sanitized report and, with the user's consent, opens the repository's
pre-filled "new issue" page in the user's browser; the user presses *Submit*. The only network call
the feature makes is a read-only search for the report's 8-hex fingerprint (duplicate check), made
after the user chose "Raporu aç"; a failure skips it silently.

**Local log** (`modules\Diagnostics.cs`: `LogWriter`, `AppLog`)
- `%APPDATA%\OpenVpnQuickSearch\logs\app-YYYYMMDD.log`, UTF-8 without BOM, lines
  `yyyy-MM-dd HH:mm:ss.fff LEVEL source message` (levels INFO / WARN / ERROR), exception lines
  indented below the entry (type, message with quoted parts removed, frames without paths).
- Thread-safe: `Write` only appends to an in-memory buffer under a lock and queues one pool work
  item to drain it; it never throws and never touches the disk on the calling thread. `Flush()`
  drains synchronously (exit, crash, before the update installer takes over). A writer that cannot
  open the file (held by an editor) retries 5 x 15 ms before dropping the chunk.
- Rotation (`LogRotation`, pure): today's file, then `app-YYYYMMDD-2.log` ... once a part reaches
  512 KB; at most 5 log files are kept. Crash / error records: at most 5 of each kind.
- Rate limiting (`LogRateLimiter`, pure): poll / list failures and repeating exceptions log at most
  once per key and 10 minutes; the next entry says how many were skipped.
- Logged: start (version, Windows build, CLR, .NET release key, scale, installed clients:
  OpenVPN / Forti / Sophos / WatchGuard as booleans, background flag; detected on a pool thread),
  exit; per provider connect / disconnect requests, outcomes and state changes (provider kind +
  outcome only, never profile, host or username); update checks, results, download / verify,
  installer start; provider poll failures; caught provider exceptions with stack; unhandled
  exceptions. Settings toggle on/off is logged.
- `forti.log` is no longer written: `FortiTrace` now writes `forti connect form: <outcome>` into the
  app log (the tunnel name and the `seçili: <tunnel>` detail are dropped). README, usage text updated.
- Demo mode: no log folder at all (`AppLog.Init(null)`), no records, no prompts, no browser.
  Verified: running the scratch build with `--demo` did not create `%APPDATA%\OpenVpnQuickSearch\logs`.
- `LogEnabled = false` turns off the log **and** the crash / error records (so also the next-start
  prompt). Existing files are kept; the user can delete them from the opened folder.

**Crash capture** (`CrashReporter`)
- `Program.Main`: `CrashReporter.Init` + `AppLog.Init` + `Install()` right after the single-instance
  mutex, before any control exists (`Application.SetUnhandledExceptionMode(CatchException)`,
  `Application.ThreadException`, `AppDomain.CurrentDomain.UnhandledException`). Main's last-resort
  catch calls `CrashReporter.Fatal`. The app uses no `System.Threading.Tasks`, so no
  `UnobservedTaskException` hook (documented in code).
- Unhandled exception: sanitized record `logs\crash-<utc>.txt` + marker `logs\crash-pending.txt`
  (record name + UTC time), log flushed. UI-thread exceptions (`ThreadException`): the app keeps
  running (like WinForms' "Continue") and asks once per run; `AppDomain` exceptions (process dies):
  the next start asks. One record per fingerprint and at most 10 per run (a repeating paint
  exception cannot flood the disk).
- Next start: the first time the panel is shown (`Shown`, so a `--background` start asks when the
  user opens the panel) `OfferPendingCrash` reads the marker; `CrashPromptLogic.ShouldOffer` (pure)
  accepts only a well-formed `crash-…txt` name, younger than 30 days, not in the future, mode `ask`,
  not demo. The marker is removed whatever the answer. Dialog: *"Önceki oturum beklenmedik şekilde
  kapandı. Hata raporunu GitHub'da açmak ister misiniz?"* + the exact contents text, buttons
  **Bir daha sorma / Sonra / Raporu aç** (Esc = Sonra). "Bir daha sorma" sets `CrashPrompt=never`
  and saves.
- Handled critical errors (exceptions caught around Forti open / disconnect, Sophos / WatchGuard
  connect / disconnect, OpenVPN start, update install start): `CrashReporter.HandledError` logs
  them (rate-limited) and keeps an `error-<utc>.txt` record; no dialog.
- Settings > Hakkında > **Son hatayı bildir**: newest crash / error record (re-sanitized with the
  names loaded now; the sanitizer is idempotent), else a "Kullanıcı bildirimi" report with the
  recent log lines; a confirmation dialog shows the contents text first.

**Report** (`ErrorInfo`, `ErrorFingerprint`, `ErrorReport`, `LogSanitizer`, `IssueLink`)
- Exception data comes from `System.Diagnostics.StackTrace` frames, not from `ex.StackTrace` text
  (Turkish .NET language packs print `konum:` instead of `at`; seen on this PC). Frames are
  `at Namespace.Type.Method in File.cs:line N` with file names only.
- Body (Markdown): kind, fingerprint, app version, Windows build, CLR + .NET release, scale,
  VPN clients var/yok, UTC minute, exception chain (type, sanitized message, frames, inner
  exceptions up to 5 deep, `AggregateException` first inner), last 60 sanitized log lines, a
  closing line saying what was removed.
- Title: `[hata] <ShortType>: <Type.Method> (<fingerprint>)`, max 160 chars; manual reports
  `[hata] Kullanıcı bildirimi (sürüm X)`.
- Fingerprint decision: **method names only.** SHA-256 over the root (innermost) exception type +
  its top 3 frames' `Type.Method`, first 4 bytes as 8 lowercase hex. No file names and no line
  numbers (stable across builds, install paths, unrelated edits); compiler counters removed
  (`<X>b__19`, `DisplayClass1a`). A wrapper (e.g. `TargetInvocationException`) does not change it.
- URL (`IssueLink.NewIssueUrl`): `https://github.com/bymrhan/vpn-manager/issues/new?template=hata-raporu.yml&labels=hata&title=…&rapor=…`,
  `Uri.EscapeDataString` (UTF-8 percent-encoding; Turkish characters as `%C4%B1` etc.). Cap
  `IssueLink.MaxUrlLength = 7000` characters for the whole URL (browsers and GitHub take about
  8 KB; the margin covers GitHub's sign-in redirect). Longer: the body is cut at a line boundary
  (binary search), an open code fence is closed, a note is appended, and the full report goes to
  the clipboard with a dialog telling the user to paste it.
- Duplicate check: `IssueLink.SearchUrl` (search API, `repo:bymrhan/vpn-manager is:issue in:title <fp>`),
  `FindDuplicate` (pure JSON parser: skips pull requests and other repositories, needs
  `(<fp>)` in the title, prefers open over closed), `CheckDuplicate(fp, fetch)` with the fetch
  injected (`UpdateClient.GetText`: HTTPS GET, only `https://api.github.com/`, 1 MB cap, 10 s
  timeout, same generic user agent as the updater). Duplicate found: "Bu hata zaten bildirilmiş
  (#n, açık/kapalı)" and that issue opens. Only URLs under `https://github.com/bymrhan/vpn-manager/issues/`
  are ever opened.

**Sanitizer** (`LogSanitizer`, the core)
- (a) Known values from memory → stable numbered placeholders: profile names `<profil-N>`, hosts /
  server entries / credential keys `<sunucu-N>` (IP literals `<ip-N>`, `host:port` mapped to the
  same placeholder as `host`), usernames incl. saved credential usernames seen this run
  `<kullanici>`, `Environment.UserName` `<kullanici>`, `MachineName` `<makine>`, domain `<alan>`,
  `%LOCALAPPDATA%`, `%APPDATA%`, `%TEMP%`, `%USERPROFILE%` → `%LOCALAPPDATA%` / `%APPDATA%` /
  `%TEMP%` / `~`. Values of 4+ characters match anywhere (case-insensitive, longest first), 3
  characters only as whole words, shorter ones are ignored.
- (b) Patterns: PEM blocks `<pem>`, inline OpenVPN `<key>/<cert>/<ca>/<tls-auth>/…` blocks
  `<anahtar>`, URL credentials, e-mail `<eposta>`, `Authorization:` / `Bearer` / `Basic`,
  `password|pass|pwd|parola|şifre|token|secret|api_key|cookie|session…` `=`/`:` values,
  `-p / --password / --token / /p <value>`, GitHub tokens `<gizli>`, UNC hosts, `X:\Users\<name>`
  → `~`, `*.ovpn` file names `<dosya>.ovpn`, IPv4 / IPv6 (incl. zone ids) `<ip-N>`, hex ≥ 32 and
  base64 ≥ 40 (with a digit) `<blob>`, host-like `name.tld` (last label lowercase letters; file
  extensions such as `.cs .log .json .exe .ps1` and `github.com` hosts excepted) `<sunucu-N>`.
  Exception messages additionally lose everything in quotes (`'<metin>'`).
- (c) Stack frames: no absolute paths (file name only). A line that is exactly one of our frames
  (identifiers only) is left untouched, so a profile named like a code word cannot garble the stack.
- Placeholders are protected from later passes, which makes `Sanitize` idempotent (tested).

**Settings** (`modules\Settings.cs`): `LogEnabled` (default `true`), `CrashPrompt` (`ask` default |
`never`; anything else normalizes to `ask`), loaded / saved like the other keys; old
`settings.json` files get the defaults (tested).

**Settings UI** (`modules\SettingsWindow.cs`, Hakkında page, after GÜNCELLEME): card **TANILAMA**
with rows *Hata kaydı tut* (toggle), *Çökmeden sonra rapor öner* (toggle, ask/never), *Günlük
klasörünü aç* (Aç), *Son hatayı bildir* (Aç), then a note with the contents summary. The rows sit in
the `SPage` stack, so they scroll with the slim scrollbar (checked in a `--demo` screenshot).
`IsDirty` includes both keys; saving applies `LogEnabled` at once.

**GitHub side**: `.github/ISSUE_TEMPLATE/hata-raporu.yml` (Turkish issue form, label `hata`,
fields `rapor` (required, pre-filled by the app), `ne-yapiyordunuz`, `ek-bilgi`; both free-text
fields ask for no contact data, names, hosts or passwords) and `config.yml`
(`blank_issues_enabled: true`). The form applies the label even for reporters without triage
rights (the `labels=` URL parameter alone would be ignored for them).

## Files

New: `modules\Diagnostics.cs`, `.github\ISSUE_TEMPLATE\hata-raporu.yml`, `.github\ISSUE_TEMPLATE\config.yml`,
`docs\TRIAGE.md`, this report.
Changed: `OpenVpnQuickSearch.cs` (Main setup, logging calls, `FortiTrace`, settings wiring),
`modules\Settings.cs`, `modules\SettingsWindow.cs`, `modules\GlassDialog.cs` (`ShowChoice`: own
button labels and Esc index; buttons widen to fit their label, existing dialogs unchanged since their
labels are narrower than the 92 px minimum), `modules\Updater.cs` (`UpdateClient.GetText`),
`tests\Tests.cs`, `README.md`, `paket\OpenVPN-Hizli-Arama-Kullanim.txt`.
Sources kept UTF-8 BOM + CRLF (`README.md` / `docs\*.md` stay without BOM like before).

## Tests

`tests\run-tests.ps1`: **292 passed, 0 failed** (219 existing + 73 new), run three times.
New groups: sanitizer removes every seed of a log full of fake secrets (profile / customer names,
`.example` hosts, documentation IPv4 / IPv6 ranges, e-mail, usernames, machine / domain, user
folders of a known and an unknown user, UNC, `password=`, `-p`, `token=`, `secret:`, GitHub token,
`Authorization: Basic`, `Bearer`, URL credentials, hex / base64 blobs, PEM, inline `<key>`,
`.ovpn` names, unknown `name.example.com.tr`), the same with no known values; idempotent (same and
fresh instance); placeholder numbering stable inside one instance; longest-first and whole-word
rules; code names, file names, `api.github.com`, versions and timestamps untouched; frame lines
untouched; fingerprint (8 hex, independent of file / line / compiler counters / 4th frame, changes
with type or top-3 method, root exception wins, equal for two real throws, frames have no paths);
report (title format, env lines, last 60 lines only, no seed or `:\` in body or title, quoted
message text removed, re-sanitizing changes nothing, record round trip, broken records); issue URL
(template + label, Turkish and reserved characters percent-encoded, round trip, truncation under
7000 with closed fence and note, only this repo's issue URLs open); duplicate parser (PR / other
repo skipped, open preferred, closed counts, other fingerprint, broken JSON / rate-limit JSON,
invalid fingerprint makes no request, network error = null, search URL contents); rotation
(parts, new day, keep 5, other files untouched, records per kind, newest record); rate limiter
(window, separate keys, skipped count, clock going back); crash marker (valid, never, demo, old,
future, path in name, error record, broken time); log file (format, multi-line message on one line,
exception frames without paths, no BOM, recent lines, disabled = nothing, size rotation with Keep,
4 threads x 50 lines without loss, unwritable folder does not throw); settings keys.

## Checked only statically / not checked

- No run of the app outside `--demo` (as required): the non-demo log, record and marker paths were
  exercised with a scratch harness against a temp folder (start / provider lines, handled error
  record written once per fingerprint, fatal record + marker, `ShouldOffer` on that marker, the
  sanitized record, URL length) and by the unit tests; the real `%APPDATA%` log, the next-start
  prompt inside the app, the `ThreadException` prompt and the browser opening were not run.
- The browser was never opened and GitHub was never contacted: the pre-filled issue form
  (`template=` + `rapor=` prefill, label application, the 7000-character cap in practice) and the
  live search API response shape are from GitHub's documented behaviour, not observed.
- The issue form YAML was not validated by a YAML parser (none on this PC) or by GitHub.
- The connect / poll logging lines inside the provider flows were compiled and reviewed but not
  driven against real FortiClient / Sophos / WatchGuard / OpenVPN (demo never reaches them).
- Visual checks done: the TANILAMA block on the Hakkında page (dark theme, scrolled with the slim
  scrollbar), the demo "Son hatayı bildir" dialog, and the crash prompt dialog with three buttons.
  Light theme and high DPI were not captured.

## Concerns

- The sanitizer is pattern based. Free text it cannot recognise (a customer name that is not a
  loaded profile, inside an unquoted log message, or a hostname without a dot) can pass. Mitigations:
  the app's own log lines carry no names by design, exception messages lose quoted text, the user
  sees the full report before submitting, and the issue form tells them to remove anything left.
  The local `app-*.log` files themselves are not sanitized (only reports are).
- Known-value replacement over-redacts: a Windows user called `user` or a profile called `Test`
  turns those words into placeholders everywhere in the report (stack frames are protected).
- The duplicate check sends the 8-hex fingerprint to `api.github.com` (unauthenticated search API,
  10 requests / minute); this is the one call the app makes, after consent.
- Fingerprints change when one of the top 3 methods is renamed or refactored; such issues must be
  linked by hand (TRIAGE.md says so).
- `LogEnabled=false` also disables crash records, so a user who turns logging off gets no crash
  prompt; "Son hatayı bildir" then offers a log-less manual report.
- `GlassDialog` buttons now widen to their label width (no change for the existing short labels).
- The label `hata` must exist in the repository for the form to apply it
  (`gh label create hata …`, in TRIAGE.md).

## How a maintainer sees reports

Issues labelled `hata` with titles `[hata] NullReferenceException: SearchForm.ConnectForti (ab12cd34)`.
`gh issue list -l hata`; same bug: `gh issue list -l hata --state all --search "ab12cd34 in:title"`.
The body has the environment, the exception chain with frames and the last 60 log lines (provider
kind + outcome per step). Workflow (dedupe, privacy check, reproduce with `--instance=dev
--settings=…`, fix with a test, close with the release version) is in `docs\TRIAGE.md`.

## Review fixes (second round)

Commits `5868a46`, `b967026`, `8496ea8`, `b7ce438`, `16b4176` (+ this section). `tests\run-tests.ps1`:
**335 passed, 0 failed** (292 before + 43 new checks), run twice.

Important items
1. **Turkish case variants.** Known values are no longer matched with `IgnoreCase`: each value is
   compiled letter by letter, `i I İ ı` -> `[iIİı]`, `ş/s`, `ğ/g`, `ü/u`, `ö/o`, `ç/c` as one class
   each (ASCII spellings match too), other letters `[xX]`, blank runs -> `\s+`. Placeholders are
   looked up by a fold key (same folding), so every variant gets the same `<profil-N>`. Tests:
   `KARŞIYAKA BELEDİYESİ`, `İzmir Liman`, `ışık enerji`, `Çağ  Tekstil` in upper / lower / title /
   ASCII / double-space / tab forms. Reviewer script t2: every variant -> its placeholder.
2. **Usernames.** `AddUser("ACME\jdoe")` also seeds `jdoe` and the domain `ACME`;
   `AddUser("x@y")` seeds `x`. Patterns: `user@host` with a dotless host or an IP
   (`<kullanici>@<sunucu-N>` / `<kullanici>@<ip-N>`), `DOMAIN\user` -> `<alan>\<kullanici>` (left side
   2-15 chars, not after `\ / :`, not a folder word such as Users / Windows / logs). Paths
   (`C:\Users\…`, `logs\app.log`, `%APPDATA%\…`) stay as before (tested).
3. **Hosts in any case.** The last label is matched case-insensitively; a file-extension "TLD" is
   kept only for single-dot tokens (`Connection.cs`). Exceptions to avoid garbling .NET names: tokens
   starting with `System.` / `Microsoft.`, and a leading exception type on a line
   (`    System.IO.IOException: …`) stays readable while the rest of the line is sanitized. Tests:
   `VPN.CORP.EXAMPLE.COM`, `gw.example.ZIP`, `portal.firma.md`, `x.firma.zip`, `Vpn.Firma.Com.Tr`.
4. **Regex time.** Every line / message is cut to 1000 chars before any pattern runs; Email and
   OvpnFile have left anchors; every regex (including the known-values one) has a 250 ms match
   timeout and a line that still times out becomes `<satir-kisaltildi>`. Reviewer t4 / t5 (up to
   50 000-char tokens): max 171 ms per input (was seconds to minutes); t6 (600 profiles x 60 lines):
   174 ms. Test: five 50 000-char shapes each under 1 s with no seed in the output.
5. **Local logs are unsanitized**: one sentence in README (and the usage text). Update failures go
   through `LogSanitizer.StripQuoted` before logging (`check failed`, `download / verification
   failed`, `install start failed`). Other paths that reach the local log with exception text
   already go through `LogWriter.ExceptionLines` (quotes stripped); connect logs carry no message text.
6. **Wording.** README, `CrashReporter.ReportContents` / `ReportSummary` and the usage text now say
   the sanitized report reaches GitHub inside the new-issue page address when the page opens and is
   published only when the user presses Submit. README and TRIAGE agree that release builds have
   no PDB, so frames carry no file / line.

Minor items
- Duplicate check: `ctx.Post` in try/catch, `Application.UseWaitCursor` while it runs, repeat
  clicks ignored while pending.
- `OnThreadException` computes the fingerprint first and skips the report work for a repeat.
- `LogWriter`: `Drain` and `Flush` take the buffer while holding the file lock (ordered chunks);
  the file is opened with `FileSystemRights.AppendData`.
- `AddProfile` also seeds the `.ovpn` file name (without extension) and its parent folder name.
- More secrets: `password: phrase with blanks` (and pwd / passwd / secret / parola / şifre: to end of
  line), `user=` / `username=` / `login=`, `key=`, `X-…-Token`, `token <value>`, cookies /
  `SVPNCOOKIE=`, `-u user:pass`, `-n <name>`, `-h <host>`.
- IPv4 accepts leading zeros (`192.168.001.010`); four-part versions after `v` / `ver` / `version` /
  `sürüm` / `release` / `build` / `clr` stay; UNC share names become `<metin>`; other absolute
  paths keep only the file name (`<yol>\file`).
- No seeds shorter than 4 chars or equal to common log words (info, user, admin, vpn, connect …);
  tested with `KnownCount == 0`.
- "Sonra" keeps the crash marker (`offers=N` line) and the next start asks again, 3 times in all;
  `CrashPromptLogic.AfterLater` is pure and tested.
- "Bir daha sorma" chosen while the settings window is open survives saving that window.
- `FortiTrace` logs the outcome with a non-identifying detail (exception type / form state, "tunnel
  selected" instead of the name) and the last detail when the form never got ready in 20 s.
- `tests\Tests.cs`: the `Run(...)` line that shared a line with `Console.WriteLine()` is split.
- `SNote` measures its wrapped text and sizes itself (checked in `--demo` at scale 1 and 1.5).

Extra task (Sophos / WatchGuard tab)
- `VpnProviders.ShowsExtSegment(enabled, installed, rows)` = enabled and (installed or rows > 0);
  the main window uses it for Sophos and WatchGuard (OpenVPN / FortiClient unchanged), and refreshes
  the bar when the installed flags change. When the selected Sophos / WatchGuard segment has no rows
  the list shows `VpnProviders.EmptySegmentText` ("Sophos Connect'te tanımlı bağlantı yok. .pro veya
  .ovpn dosyasını Sophos Connect'e ekleyin." / "WatchGuard SSL VPN istemcisinde kayıtlı sunucu
  yok."). Demo keeps its fictional rows (installed is false in demo, rows > 0). Pure tests cover the
  rule, the texts and that an empty Sophos tab sits at index 3 (Ctrl+4) with FortiClient on.
  Not seen in the running app: demo never reports a client as installed, so the empty tab was not
  captured; only compiled and unit-tested.

Still open / concerns
- Pattern-based sanitizing: a dotless host name that is not a known value (`fw-gamma-01`), a MAC
  address, or a customer word inside free text can still pass; the user sees the report before
  Submit.
- Case-insensitive hosts over-redact dotted identifiers that are not under `System.` / `Microsoft.`
  (e.g. `SearchForm.Connect` in a non-frame line); frame lines are protected.
- A script slip during this round created an empty file `C:\Users\user\Documents\Codex\2026-11-11\watchguard-ss\README.md`
  (0 bytes, outside the repository; .NET resolved a relative path against the parent folder).
  Deleting it was blocked by the permission classifier; please delete it by hand.