# Profile fallback (2.1.2): report

Branch `feat/profile-fallback`. Problem: on a PC with OpenVPN Connect 3.6.0 the app lists no profiles.
It read only `%APPDATA%\OpenVPN Connect\config.json` in the layout seen on 3.9.x. We have no 3.6.0
sample, so this change does not bet on one guessed layout: the parser is tolerant, there are fallback
sources, a manual path, and a value-free diagnostic that shows the real layout.

## Commits
- `201512a` Profiles: tolerant config.json parsing, fallback sources, schema outline
- `42cf72e` Profiles: fallback sources in the list, manual source buttons, diagnosis
- `b9d969f` Release 2.1.2: bump version, document the profile fallback (not published)

## What changed
- **`modules\ProfileSources.cs` (new)**
  - `ProfileJson` (pure, takes JSON text):
    - `persist:root` and `status` can each be a JSON string or an object; `profiles` can be a map or an array.
    - Accepted key names: id (`id`, then the map key, then `profileId`); name (`profileDisplayName`, `profileName`, `name`, `displayName`, `title`); host (`hostname`, `remoteHost`, `host`, `server`, `remote`); file (`filePath`, `path`, `file`, `source`); user (`username`, `user`).
    - `lastConnected` / `lastConnectedAt` can be epoch ms, epoch s (below 1e11) or ISO-8601.
    - Where it looks for profiles, in order: `persist:root/status/profiles`, `profiles`, `persist:root/profiles`, `persist:root/profile/items`. If none has profiles, a breadth-first walker runs (depth at most 6, at most 5000 nodes). It takes the first collection where at least half the elements have a name key plus an id, host or file key. An empty known node does not stop the search.
    - `Parse` returns one of: Ok, BadJson, UnknownLayout, NoProfiles.
    - `SchemaOutline` gives key names and JSON types only. Ids and keys that are not plain identifiers become `<id>`. Maps keyed by id or name (`profiles`, `items`, `byName`, …) show one representative element. Depth is at most 6 and output at most 150 lines.
  - `OvpnScan`:
    - Reads only `remote`, `setenv PROFILE` / `FRIENDLY_NAME`, and the `# OVPN_ACCESS_SERVER_PROFILE=` / `_FRIENDLY_NAME=` / `friendly_name` comments.
    - Inline blocks (`<ca>`, `<cert>`, `<key>`, `<tls-auth>` …) are skipped. Only `<connection>` blocks are read, for `remote`.
    - Files over 1 MB: only the file name is used.
    - `FindFiles` is breadth-first: at most 500 files, depth 2, 200 folders, and reparse points are skipped.
  - `ProfileSources.Load(settings, out report)` never throws. The order is:
    1. `config.json` under `%APPDATA%` (primary), `%LOCALAPPDATA%`, `%PROGRAMDATA%`. The first that yields profiles wins. `%ProgramFiles%` is never read.
    2. `profiles\*.ovpn` under `%APPDATA%` and `%LOCALAPPDATA%` (folder names are case-insensitive on Windows, so `Profiles`/`profiles` is the same folder).
    3. Always added: the manual `ProfileFolder` (one subfolder level) and `ExtraProfileFiles`. These are de-duplicated by id and by file name, and marked `Origin = "manual"`.
  - Profiles from `.ovpn` files get `Id` = file name without extension and `EstimatedId = true`. Assumption: OpenVPN Connect stores `profiles\<id>.ovpn`, and `--connect-shortcut=<id>` takes that id (true on 3.9).
  - Logging: one structured `AppLog.Warn("profiles", …)` per distinct (reason, size) per run when the primary file exists but gives no profiles. It holds the version (exe FileVersionInfo plus the uninstall registry key), the size, the source used, and the outline sanitized line by line.
  - `DiagnosticText` covers app and OpenVPN Connect versions, candidates with exists / size / result, folder counts, the profile node path and the outline. Paths use `%APPDATA%/…` labels and no profile file names.
- **`ProfileStore.Load`** (backups, setup wizard, `--list`) uses the tolerant parser. As before, it throws when the file is missing, has bad JSON, or has an unknown layout. A 3.9 file with no `profiles` key is still 0 profiles.
- **Settings**: `ProfileFolder` (string) and `ExtraProfileFiles` (string array). A missing key or wrong type gives an empty value. `Clone` deep-copies the list; `Normalize` trims and de-duplicates, at most 500.
- **Main window**
  - `ReloadProfiles` uses `ProfileSources`. Before, a failed OpenVPN read also dropped the Forti/Sophos/WatchGuard rows; now those still load.
  - A probably half-written `config.json` (unreadable or bad JSON) still keeps the old list and retries every 2 s, as before.
  - While a fallback or manual source is in use, the sources are read again every 30 s (`CheckConfigChanged`, rate-limited). A source change is logged once.
  - The empty state shows the reason and two glass buttons, "Profil klasörünü seç…" and "Dosya seç…". `ProfileList.EmptyActions` is painted and hit-tested by the list.
  - "Dosya seç…" stores the files in `ExtraProfileFiles`, then calls the existing `ImportOvpns` flow (which asks first) to add them to OpenVPN Connect. No new CLI flag.
  - If another provider fills the list while OpenVPN profiles cannot be read, the status line shows a hint.
  - Connect timeout on a profile with an estimated id: a warning log line, plus a status hint ("profil kimliği tahmini …").
  - `DeleteProfile` refuses estimated-id profiles, because `--remove-profile` also accepts a name, so a guessed id could remove another profile.
  - `--demo-empty` shows the empty state in demo mode.
- **Settings › Genel › "PROFİL KAYNAĞI"**: status line with "Yeniden tara", profile folder with "Klasör seç…", "Kaynağı sıfırla", and "Tanı bilgisini kopyala". The copied text passes through `CrashReporter.NewSanitizer()`. In demo mode nothing is read.
- **Docs**: README has the "Profiller okunamazsa" section, the settings line, the demo switch, the module table, the version list and the English summary. RELEASE-NOTES has `## 2.1.2`. AssemblyInfo is 2.1.2 (all three attributes).

## Tests
`tests\run-tests.ps1`: **548 C# passed, 0 failed** (was 464, 84 new) and **28 install tests passed**. New test groups:
- 3.9 layout checked field by field against the old parser, which is kept inside the test.
- Tolerant variants: string/object levels, map/array, alternate keys, other levels, walker, depth bound.
- Timestamps.
- Reasons and status texts.
- Outline never contains values (fake secrets, PEM, IP, names, numeric ids).
- `.ovpn` directive scan: fake cert/key data and a `remote` line inside `<ca>` do not leak.
- `FindFiles` bounds.
- Source order and manual sources, using temp `%APPDATA%`/`%LOCALAPPDATA%`/`%PROGRAMDATA%` overrides.
- The diagnostic text contains no names.
- Settings back-compatibility.

## Concerns / not verified
- **Not verified visually.** I compiled a scratch build in TEMP (exit 0), but the coordinator stopped all GUI runs before any window was captured. The empty-state buttons, the Settings block layout (long button texts, ellipsized descriptions) and the dialogs have not been seen on screen.
- Not tested against a real OpenVPN Connect 3.6.0 file. Whether 3.6.0 keeps `profiles\<id>.ovpn`, and whether `--connect-shortcut` takes that id or a name, are assumptions. The connect-timeout hint covers the case where they are wrong.
- When a `.ovpn` file has no name directive and its file name looks like an id, the name comes from the `remote` host. Otherwise the file name is used, not the host: I deliberately put the file name before the host, which differs from the order in the brief.
- The walker's match rule (a name key plus an id, host or file key) is looser than "name and id", so it can match collections whose elements have no id.
- A map keyed by plain-identifier names under an unknown key that does not look like a collection could still show a key name in the outline. The report sanitizer runs afterwards as a second layer.
- The log line is sanitized. Its outline part can be long (up to 80 outline lines joined on one line).
- The scan runs on the UI thread but is bounded (500 files, 200 folders); while degraded it repeats every 30 s.
- Not published; `build.ps1` was not run.
