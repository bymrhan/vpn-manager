# Triaging error reports (`hata`)

How reports reach the repository and how to work through them. The user-facing description is in
the README section *Hata raporları ve günlükler*.

## Where reports come from

- After a crash (or any time via *Ayarlar > Hakkında > Son hatayı bildir*) the app opens
  `https://github.com/bymrhan/vpn-manager/issues/new?template=hata-raporu.yml&labels=hata&title=…&rapor=…`
  in the user's browser. The user reads the report and presses *Submit new issue*. The app itself
  never uploads anything: there is no token in the app (it would be extracted from a public binary
  at once) and the reports come from customer VPN work, so the user must see what leaves the PC.
- The issue form `.github/ISSUE_TEMPLATE/hata-raporu.yml` applies the `hata` label even when the
  reporter has no triage rights (a `labels=` URL parameter alone is ignored for them).
- Title: `[hata] <ExceptionType>: <Type.Method> (<fingerprint>)`. Reports without a recorded error
  ("Son hatayı bildir" with an empty log folder) are titled `[hata] Kullanıcı bildirimi (sürüm X)`
  and have no fingerprint.
- The report body: environment (app version, Windows build, CLR / .NET release, scale, which VPN
  clients are installed), the exception chain (type, sanitized message, frames as
  `at Type.Method in File.cs:line N`), the last 60 sanitized log lines.
- Release builds are compiled without a PDB, so frames normally have no file / line part.

## Fingerprint

8 hex characters: SHA-256 of the root (innermost) exception type and its top 3 frames' type +
method names, with compiler counters removed (`<Connect>b__19` -> `<Connect>b__`,
`DisplayClass1a` -> `DisplayClass`). No file names, no line numbers: the same bug keeps its
fingerprint across builds, install folders and unrelated edits. A change in any of the top 3
methods (renames, a refactor that moves the throw) gives a new fingerprint; link such issues by hand.

Before opening the browser the app runs one read-only search
(`api.github.com/search/issues?q=repo:bymrhan/vpn-manager is:issue in:title <fingerprint>`); when an
issue (open preferred, else closed) carries `(<fingerprint>)` in its title, the user sees "Bu hata
zaten bildirilmiş" and that issue opens instead. Offline or rate-limited: the check is skipped, so
duplicates still happen.

## Daily workflow (gh CLI)

```powershell
# new and open reports
gh issue list -l hata
# everything with one fingerprint, closed ones too
gh issue list -l hata --state all --search "ab12cd34 in:title"
# read one
gh issue view 42
```

1. **Dedupe.** Search the fingerprint (above). Keep the oldest issue, close newer ones with
   `gh issue close 57 --reason "not planned" --comment "#42 ile aynı hata (parmak izi ab12cd34)."`
   and add any new detail from them to #42.
2. **Check the privacy of the report.** The sanitizer is pattern based. If a customer name, host,
   username or anything personal slipped through, edit the issue body at once (and the title), then
   fix `LogSanitizer` with a test in `tests\Tests.cs` (the `Seeds` list) so it cannot happen again.
   The edit history of an issue body is public: after editing, delete the old revision from the
   history (maintainers can), and for a real secret tell the reporter to rotate it.
3. **Reproduce.** Use the version and the installed clients from the report. The log lines show the
   provider kind and the outcome of each step (`forti connect outcome: FillFailed`,
   `watchguard poll returned no state`, `update check failed: …`). Run a local build next to the
   installed one without touching it: `--instance=dev --settings=%TEMP%\vpnm-dev.json`. `--demo`
   never logs and never opens the browser, so it cannot reproduce provider bugs.
4. **Fix** with a test where the code allows it (`tests\run-tests.ps1`).
5. **Close with the release.** After the release that contains the fix:
   `gh issue close 42 --comment "v2.1.1 ile düzeltildi."`. A new report with the same fingerprint
   from a version at or above the fix means the fix did not hold: reopen the original issue.

## Labels

- `hata`: every report from the app (from the issue form). Create it once:
  `gh label create hata --color d73a4a --description "Uygulamanın gönderdiği hata raporu"`.
- Optional: `cokme` for unhandled crashes (body line `Tür: çökme`), `yineleme` for duplicates.

## Where the data lives on the user's PC

`%APPDATA%\OpenVpnQuickSearch\logs\`: `app-YYYYMMDD.log` (at most 5 x ~512 KB),
`crash-<utc>.txt` / `error-<utc>.txt` (sanitized report records, at most 5 each) and
`crash-pending.txt` (the marker that makes the next start offer the report). A user can send the
folder by other means if GitHub is not an option; the `app-*.log` files are **not** sanitized
(only reports are), so ask them to read them first.
