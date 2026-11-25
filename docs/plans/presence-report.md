# Presence: Tepsi / Tepsi + düğme / Görev çubuğu (2.1.1)

Branch `feat/presence`. User request: in Settings, choose tray or taskbar button; the taskbar button
must stay for as long as the app runs.

## What changed

- `AppSettings.Presence` (string): `tray` (default), `both`, `taskbar`. Replaces the bool
  `ShowInTaskbar`. `SettingsStore.Load`: if `Presence` is present it is used (invalid or wrongly
  typed => `tray`); if missing, the legacy `ShowInTaskbar` key is read (`true` => `both`, else
  `tray`). `Save` writes only `Presence`. The user's settings.json (`ShowInTaskbar=true`) loads as
  `both` (= exactly 2.1.0 behaviour) and is rewritten with `Presence` on the next save.
- `PresenceMode` (modules\Glass.cs): pure decisions, all tested: `Effective` (demo without
  `--settings=` keeps `tray`, same rule as 2.1.0), `TrayIcon`, `Button`, `HideMinimizes`,
  `CloseExits`, `StartsMinimized(mode, background, openVpnShowing)`, `FromLegacy`, `Normalize`.
- `TaskbarButton`: new `Style`/`SetMinimizeBox`/`HasMinimizeBox` (WS_MINIMIZEBOX), `Iconic`,
  `Restore(activate)`. `TaskbarButton.Wanted(bool, ...)` was replaced by `PresenceMode`.
- Settings > Genel: the toggle row became "Uygulama nerede görünsün" with an `SSegmented`
  (Tepsi / Tepsi + düğme / Görev çubuğu) and a description line that follows the selection, exactly
  like the update-mode row. Deviation: the three labels make the control 329 px wide (at 100 %),
  which would leave ~80 px for the title beside it, so `SRow` got a `Stacked` option (title +
  description on top, the choice below, row height 106). Checked on a screenshot of the scratch build.

## Behaviour per mode

| | tray | both | taskbar |
|---|---|---|---|
| Tray icon (`NotifyIcon.Visible`) | yes | yes | no |
| WS_EX_APPWINDOW | no | yes | yes |
| WS_MINIMIZEBOX | no | no | yes |
| Hide (Esc, ×, –, hotkey, tray click, connect auto-hide, OpenVPN window gone) | Hide() | Hide() (button goes) | minimize to the button |
| Alt+F4 / SC_CLOSE | hides | hides | exits |
| `--background` start | hidden | hidden | minimized (docked/shown if OpenVPN's window is showing, as in every mode) |
| Second launch / hotkey / OpenVPN reappears | show | show | restore (re-docks: `userMoved=false`) |

- WS_MINIMIZEBOX only in taskbar mode, so `both` stays identical to 2.1.0 (a minimized window there
  would vanish into the tray on hide anyway). The borderless window has no caption, so the box is
  never drawn; it only lets the taskbar button minimize/restore the window.
- Live switch after Kaydet (`ApplyPresence`): same handle; tray icon via `NotifyIcon.Visible`,
  button via the existing ex-style flip, minimize box via GWL_STYLE. Leaving taskbar mode while
  minimized restores first; entering taskbar mode while hidden shows the window minimized.
- "On screen" is now `OnScreen = Visible && !IsIconic` and replaces `Visible` where the meaning was
  "panel open": tray click, hotkey toggle, `Sync` rising/falling, `RaiseAbove`, trim timers,
  Forti/Sophos/WatchGuard polling, update idle check, `ShowSettings`. `UpdateDockState` returns early
  while iconic (minimized bounds are -32000); restore runs `OnRestored` (from WM_SIZE, deferred):
  list animation, `UpdateDockState`, ambient wake, pending crash offer.
- Exit in taskbar mode: ⋯ > Çıkış (already in the main menu), Alt+F4, the taskbar's close.
- Logoff/shutdown: unchanged. WM_QUERYENDSESSION/WM_ENDSESSION give CloseReason.WindowsShutDown,
  which is never cancelled in any mode. A raw WM_CLOSE (Task Manager, some shells) is
  TaskManagerClosing in WinForms and also exits in every mode (unchanged from 2.1.0).

## Notices while minimized (taskbar mode)

- Update notice (`UpdateNotice`) and issue notice (`IssueNotice`) are unowned corner forms that
  exist today: they appear while minimized, unchanged.
- Toasts / island are controls inside the window: seen on restore (status stays in the island).
- The backup balloon needs the tray icon: skipped when the icon is hidden (status/toast still set).
- Crash offer of the previous session: shown on the first restore, not while minimized
  (`OfferCrashWhenOnScreen`), matching tray mode ("first time the panel is shown").
- Fix: `GlassDialog.Show/ShowChoice` and `ReportPreview` restore a minimized owner first, so no modal
  dialog is left owned by a minimized (therefore hidden) window. Hidden owners (tray) untouched.

## Verification

- `tests\run-tests.ps1`: 464 C# checks passed (was 431; new: settings default/round trip/legacy
  migration/invalid values/written keys, PresenceMode decisions, minimize-box style and handle
  stability) + install.ps1 tests 28 passed.
- Scratch build in the session scratchpad (csc line + resources from build.ps1), run only with
  `--demo --instance=... --settings=<scratch file>`, killed by PID; a small EnumWindows harness:
  - startup styles: tray = no APPWINDOW/MINIMIZEBOX; both and legacy `ShowInTaskbar=true` file =
    APPWINDOW only; taskbar = APPWINDOW + MINIMIZEBOX.
  - taskbar: Esc => iconic; second launch => restored, foreground, same bounds (700,156,1220,876);
    SC_MINIMIZE / SC_RESTORE (taskbar clicks) work; SC_CLOSE (Alt+F4) => process exits;
    `--background` => visible + iconic, not foreground; restore => placed bounds.
  - tray / both: SC_CLOSE => hidden, process alive; second launch => shown; `--background` (tray) => hidden.
  - live switch through the settings UI (clicking the choice + Kaydet): tray => taskbar, taskbar =>
    tray, taskbar => both; same HWND each time, styles flipped, scratch file got `Presence`; Esc
    afterwards behaves per the new mode.
- Not verified: the tray icon's visibility itself (no shell query in the harness; it is
  `NotifyIcon.Visible`), docking next to a real OpenVPN Connect window (demo mode never docks),
  the real Ctrl+Alt+V (the installed app owns the hotkey), real logoff.

## Other

- Version bumped to 2.1.1 (AssemblyInfo), README (Turkish section, settings list, Esc row, version
  line, English summary) and docs\RELEASE-NOTES.md "## 2.1.1". RELEASING.md unchanged. No build.ps1
  run, no package, nothing pushed.
- `modules\AssemblyInfo.cs`, README.md and RELEASE-NOTES.md were already LF in the working copy;
  `.gitattributes` normalizes them (git warned on add).
