# Multi-VPN support: Sophos Connect and WatchGuard SSL VPN

Goal: the app (WinForms, C# 5, `csc.exe` v4.0.30319; no `?.`, no `$""`) already lists and drives OpenVPN Connect and FortiClient profiles. Add Sophos Connect and WatchGuard Mobile VPN with SSL as two more providers: detect the installed client, list its connections in the same list, connect and disconnect from the app, show connected state, record history.

Spec: the user's request "sophos ve watchguard ssl vpn uygulamalarını da tespit edip buradaki bağlantıları alabilsin yönetebilsin" and "bağlantılar için forti veya openvpn'i öne getirmesin, arkada kendi otomatik yapabilsin".

## Global Constraints

- Build: `powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1` must pass (all existing tests plus new ones). Build the app with `build.ps1 -NoProfiles` only when a task says so. Run builds from PowerShell, not Git Bash (`/` csc flags get mangled).
- Sources: UTF-8 with BOM, CRLF line endings. C# 5 only. New code goes in `modules\*.cs` (build and tests glob `modules\*.cs`).
- Never break existing OpenVPN/FortiClient behaviour. Follow the `FortiStore` / `FortiGui` / `FortiCredentialStore` patterns in `modules\FortiClient.cs` and `modules\FortiCredentials.cs`.
- Never start a real VPN connection or disconnect the user's current one while testing. Read-only probes of the installed clients are fine. Never print, log or store passwords. Never write real hostnames, IPs or usernames into the repo, tests or docs: use fictional `.example` data.
- Windows are driven in the background: use `QuietWindow` (`modules\QuietWindow.cs`) so no client window comes to the front. A window the user already has open is left alone.
- Provider ids: `sophos:<connection name>` and `watchguard-ssl:<server>`. Profile `Source` is empty for these, like Forti.
- Demo mode (`--demo`) must never touch Sophos or WatchGuard.
- Do not edit `README.md` screenshots. Doc changes only in the last task.

## Facts found on this PC (read-only probes)

Sophos Connect 
- Install dir `C:\Program Files (x86)\Sophos\Connect` (`sccli.exe`, `scvpn.exe`, `scgui.exe`). Registry `HKLM\SOFTWARE\WOW6432Node\Sophos\Connect`.
- CLI `sccli`: `list`, `enable -n NAME [-u USER -p PASS -c PFX]` (connects), `disable -n NAME` (disconnects, success if already disabled), `get -n NAME -t all|config|endpoints|network|dynamic|security`, `show -v`, `add -f FILE`, `remove`. `sccli list` prints `Connections:` then `  None` when empty (no profile on this PC, so the real listing format is unverified: parse defensively and unit-test with fixtures built from `sccli get`/`list --help` knowledge; document the assumed format).
- Daemon `scvpn.exe` runs as a service; `scgui.exe` is the tray GUI.

WatchGuard Mobile VPN with SSL
- Install dir `C:\Program Files (x86)\WatchGuard\WatchGuard Mobile VPN with SSL` (`wgsslvpnc.exe` GUI client, `wgsslvpnsrc.exe` service, bundled `openvpn.exe`, TAP driver). Process `wgsslvpnsrc` runs as service.
- Single-connection client. Settings in `HKCU\Software\WatchGuard\SSLVPNClient\Settings`: `Server` (comma separated `host:port` list, trailing comma), `Username`, `RememberMe`, `AutoReconnect`, `UseSAML`. Downloaded config under `%APPDATA%\WatchGuard\Mobile VPN\` (`client.ovpn`, `client.wgssl`, certs, `wgsslvpnc.log`).
- No known CLI. The client is a native Win32 dialog (not Chromium). Connect must be driven through its window (UI Automation or Win32 messages) in the background, or via whatever the investigation in Task 3 proves works. Connected state: the log `wgsslvpnc.log`, the TAP adapter being Up, and/or the `openvpn` child process of the service.

## Tasks

### Task 1: Close the background FortiClient window instead of revealing it

`QuietWindow.Reveal(false)` is called after a FortiClient connect succeeds (in `OnFortiState`) and 4 s after a disconnect is pressed (in `DisconnectForti`). That moves the parked off-screen window onto the screen, so the app "comes to the front" at disconnect. Fix: when the window is one this app parked (`QuietWindow.IsParked`), end the FortiClient GUI process instead (FortiClient.exe GUI only, as `FortiStore.StopGui` does; VPN service and tray are separate processes) and forget the parked handle without moving it. Reveal with `activate: true` stays only for "user must act" paths (password, fill failure, 25 s no connection, errors). A window the user had open on screen is never parked, never closed.

Files: `modules\QuietWindow.cs` (add `Forget()`), `modules\FortiClient.cs` (add `public static void CloseParkedGui()` which stops the GUI only when `QuietWindow.IsParked`, then `Forget()`), `OpenVpnQuickSearch.cs` (the two call sites).
Done when: tests pass; no code path moves a parked window on-screen without `activate: true`; `grep Reveal(false)` has no remaining hits.

### Task 2: Sophos Connect provider

New `modules\SophosConnect.cs` with `SophosStore` mirroring the `FortiStore` surface the app needs: `IsInstalled` (sccli.exe exists), `IsSophos(VpnProfile)`, `Load()` returning `VpnProfile`s (Id `sophos:<name>`, Name, Host from `sccli get -n NAME -t config` when available, `Initials`/`Keys` set the way `FortiStore.Load` does), `Connect(VpnProfile, FortiCredential-like creds optional)` via `sccli enable -n NAME` (run hidden, no window, with timeout; password only through `-p` when a saved credential exists, never logged), `Disconnect(name)` via `sccli disable -n NAME`, and `ConnectedName()` (the connection whose state is connected; derive from `sccli get`/`list` output, with an adapter fallback). Run `sccli` with `CreateNoWindow`, redirect output, 10 s timeout, never block the UI thread (callers use the thread pool). Parsing lives in pure static methods so tests feed canned text. Add tests in `tests\Tests.cs` for parsing (empty list, several connections, garbage). Credentials: reuse `FortiCredentialStore` with target prefix distinct per provider (add an optional prefix parameter or a sibling class; do not change Forti's stored targets).

Files: `modules\SophosConnect.cs`, `modules\FortiCredentials.cs` (only if a prefix parameter is needed, backward compatible), `tests\Tests.cs`.
Done when: tests pass; probing `SophosStore.Load()` on this PC returns an empty list without error.

### Task 3: WatchGuard SSL VPN provider

First investigate (read-only): `wgsslvpnc.exe` command line options (strings in the exe, `/?`-style probing is allowed only if it cannot start a connection), window class and controls of the running client if its window exists (UI Automation tree via `System.Windows.Automation` or Win32 `EnumChildWindows`), and how `wgsslvpnc.log` marks connected/disconnected. Record findings in `docs\providers\watchguard-ssl.md` (no real hosts or users).

Then new `modules\WatchGuardSsl.cs` with `WatchGuardStore`: `IsInstalled`, `IsWatchGuard(VpnProfile)`, `Load()` (one profile per `Server` entry in `HKCU\Software\WatchGuard\SSLVPNClient\Settings`, name `WatchGuard SSL VPN` plus host when several, Username from the registry value), `Connect(profile, credential)` driving the client in the background through the approach the investigation proves (CLI if one exists, otherwise fill the dialog's server/username/password and press Connect through UI Automation / Win32 messages while the window is parked off-screen via `QuietWindow`), `Disconnect()`, `ConnectedServer()` (adapter/log/process based, cheap, called every few seconds from a worker thread). Credential store prefix distinct from Forti's. If the dialog cannot be driven reliably, implement detection plus "open client, pre-selected" and report DONE_WITH_CONCERNS with what failed. Parsing helpers pure and unit-tested in `tests\Tests.cs`.

Files: `modules\WatchGuardSsl.cs`, `docs\providers\watchguard-ssl.md`, `tests\Tests.cs`.
Done when: tests pass; `WatchGuardStore.Load()` on this PC returns the profile(s) from the registry without starting anything.

### Task 4: Integrate both providers in the app

In `OpenVpnQuickSearch.cs` and the list/UI modules: merge `SophosStore.Load()` and `WatchGuardStore.Load()` results in `ReloadProfiles` (skipped in demo mode), route `Connect` / `DisconnectCurrent` / tray and context menus by provider (follow how `FortiStore.IsForti` is dispatched; each provider gets `ConnectX`, `DisconnectX`, state polling next to `PollForti`, `OnXState`, history recording like `RecordFortiSession`, "connected" island/header state, quiet windows), segments in the filter bar appear only for installed providers (`Tümü, OpenVPN, FortiClient, Sophos, WatchGuard`), a small provider badge or label on list rows consistent with how Forti rows differ, Settings toggles to enable/disable each detected provider (like the FortiClient toggle), reachability uses the profile host when known. Add fictional Sophos and WatchGuard rows to `Demo.cs` (`--demo` only). Existing tests keep passing; add tests for provider routing helpers.

Files: `OpenVpnQuickSearch.cs`, `modules\*.cs` as needed (`GlassList.cs`, `Settings.cs`, `SettingsWindow.cs`, `Demo.cs`), `tests\Tests.cs`.
Done when: `tests\run-tests.ps1` passes; `build.ps1 -NoProfiles` builds; the app starts with `--demo` and with `--background` without errors; with real data it lists the WatchGuard profile and no Sophos rows.

### Task 5: Update check, update preferences, silent update

Users who installed the app (via `install.ps1`, GitHub release zip, install dir `%LOCALAPPDATA%\Programs\OpenVPN Hizli Arama`) get updates from GitHub Releases of `bymrhan/vpn-manager`.

New `modules\Updater.cs` (pure, testable parts separate from UI/IO):
- Current version from the assembly version (`modules\AssemblyInfo.cs`, 2.0.0).
- Check: HTTPS GET `https://api.github.com/repos/bymrhan/vpn-manager/releases/latest` (TLS 1.2, 10 s timeout, generic User-Agent such as `VpnManager-Updater`, no user data in the request, never on the UI thread). Parse `tag_name` (`v2.1.0`), `html_url`, `body` (release notes), `prerelease`, assets `OpenVPN-Hizli-Arama-Kurulum.zip` and `OpenVPN-Hizli-Arama-Kurulum.zip.sha256`. Ignore prereleases and drafts. Compare versions numerically (`2.10.0` > `2.9.0`).
- Settings (`modules\Settings.cs`, persisted in settings.json, backward compatible defaults): `UpdateMode` = `ask` (default: tell the user, they decide) | `auto` (download, verify and install on its own when the app is idle) | `off` (only manual "Şimdi denetle"); `UpdateInterval` = `startup` | `daily` (default) | `weekly`; `LastUpdateCheckUtcTicks`; `SkippedVersion`.
- Schedule: first check about 30 s after start, then when due by interval; one timer in the existing watcher/tick loop (no new background polling thread). Never in demo mode. Network failure is silent (retry at the next due time, status text only for manual checks).
- `ask` mode: a non-modal glass notice/toast style consistent with the existing toast/GlassDialog: "Sürüm X hazır — Şimdi güncelle / Sonra / Bu sürümü atla" plus a link/button for the release notes. "Bu sürümü atla" stores `SkippedVersion`.
- `auto` mode ("sessiz yükleme"): download the zip to a temp folder, verify SHA-256 against the `.sha256` asset (mandatory: abort with a status message on mismatch or missing hash), and install only when the app is idle: main window hidden or no keyboard/mouse input for about 2 minutes (`GetLastInputInfo`), no connect/disconnect in progress, no dialog open. A connected VPN does not block an update (OpenVPN Connect/FortiClient run independently); the app restarts in background mode afterwards (`--background`).
- Install mechanics: only when the running exe lives in the install dir; a portable copy elsewhere only notifies (opens the release page). Reuse the package's `kur.ps1` with a new quiet update switch (add it to `paket\kur.ps1` and keep the existing interactive behaviour unchanged): copy the new exe, keep settings, pins, history and profiles, no setup wizard, no admin rights, restart the app in background; if the copy fails the old exe stays (rollback). The running exe cannot overwrite itself: the app starts a hidden helper (PowerShell) then exits, or renames the running exe to `.old` first; pick the safest, document it in code.
- Settings window: a "Güncelleme" block on the About or General page (match the existing glass settings style): current version, last check time, mode choice (3 options), interval choice, "Şimdi denetle" button with status text (up to date / new version / error), link to release notes.
- `build.ps1`: also write `OpenVPN-Hizli-Arama-Kurulum.zip.sha256` next to each package zip (hash and file name, same format the existing release asset uses) so every release can carry it; add `docs\RELEASING.md` (how to cut a release: bump AssemblyInfo, `build.ps1 -NoProfiles`, `gh release create` with zip and sha256).
- Security (binding): downloads only from the github.com release assets of this repo over HTTPS; SHA-256 verification mandatory before anything runs; never execute anything that failed verification; no credentials or profile data leave the machine.
- Tests in `tests\Tests.cs`: version compare, tag parsing, release JSON fixture parsing (prerelease/draft ignored, missing asset), due-time decision for each interval, idle decision, settings round trip and defaults for an old settings.json without the new keys.

Files: `modules\Updater.cs`, `modules\Settings.cs`, `modules\SettingsWindow.cs`, `OpenVpnQuickSearch.cs`, `paket\kur.ps1`, `build.ps1`, `docs\RELEASING.md`, `tests\Tests.cs`.
Done when: tests pass; `build.ps1 -NoProfiles` builds; the check works against the real latest release (read-only GET; v2.0.0 equals current, so it reports up to date); the install path is exercised in a temp install dir with a fake package, never on the user's real install.

### Task 6: Docs

Update `README.md` (Turkish, keep structure) with every feature added since the first release: supported clients table (OpenVPN Connect, FortiClient, Sophos Connect, WatchGuard SSL VPN) with what works per client and known limits (Sophos format unverified until a profile exists, WatchGuard single connection), settings, the update system (check, ask/auto/off, interval, silent update when idle, release process), `forti.log`-style diagnostics, background-window behaviour. Add `paket\OpenVPN-Hizli-Arama-Kullanim.txt` notes only if a line is wrong now. No new screenshots.

Files: `README.md`, `paket\OpenVPN-Hizli-Arama-Kullanim.txt`.
Done when: the README mentions all four clients and no real host or user appears anywhere in the repo (`git grep` for the registry Server value is clean).
