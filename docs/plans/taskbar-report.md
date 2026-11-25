# "Görev çubuğunda göster" — implementation report

Branch `feat/taskbar` (from `main` 74e7d05). User request: the app has no taskbar button; make it a
setting and put it into the 2.1.0 update.

## Commits

- 29161d5 Settings: ShowInTaskbar key and "Görev çubuğunda göster" toggle on Genel
- 996ece2 Main window: taskbar button through WS_EX_APPWINDOW on the same handle
- 1435352 Main window: minimized-window comment no longer names a taskbar click
- ef63fb8 Docs: taskbar option, 2.1.0 release notes, RELEASING copies them into the release

## Design

- `AppSettings.ShowInTaskbar` (default false). Load: missing key or non-bool value = false; saved as
  `"ShowInTaskbar"`. Clone is MemberwiseClone; Normalize has nothing to fix for a bool.
- Settings > Genel > DAVRANIŞ: toggle row "Görev çubuğunda göster" / "Açıkken uygulama penceresi görev
  çubuğunda görünür." (Glyph.Layers) right after "Windows açılışında başlat"; included in `IsDirty`.
  The page is the SPage stack with its own scrollbar, so the extra row only lengthens the scroll range.
- The form keeps WinForms `ShowInTaskbar = false` (the property is never touched, so the handle is
  never recreated). WinForms then makes the window owned by its hidden taskbar-owner window; an owned
  window gets a taskbar button and an Alt+Tab entry exactly when it has `WS_EX_APPWINDOW`.
  - `modules\Glass.cs` `TaskbarButton`: pure `Wanted(setting, demo, settingsFilePassed)`,
    `ExStyle(current, button)` (on: +APPWINDOW, -TOOLWINDOW; off: -APPWINDOW only), `Plan(...)`
    (None / StyleOnly for a hidden window / Reshow for a visible one) and `Apply(hwnd, ...)`
    (GetWindowLong/SetWindowLong on GWL_EXSTYLE; visible window: SW_HIDE, new style, then SW_SHOW if it
    was the foreground window, otherwise SW_SHOWNA, so docked mode never gains activation).
  - `SearchForm.CreateParams` applies `ExStyle(cp.ExStyle, taskbarButton)`: correct style for the handle
    created in the constructor (`var handle = Handle`) and for any later WinForms `UpdateStyles`.
  - `ShowSettings` after Kaydet: `if (r.ShowInTaskbar != old.ShowInTaskbar) ApplyTaskbarButton();`.
    Turning the button off while minimized first restores the window (no bare minimized stub).
  - Handle-bound initialisation (RegisterAppHotkey and ApplyFrameStyle in OnHandleCreated, the
    foreground WinEvent hook, the OpenVPN process hook, activation wait) is untouched because the handle
    stays the same; `WndProc` WM_HOTKEY (`Visible && ContainsFocus` -> hide, else ShowAndFocus) is
    unaffected. A native SW_HIDE/SW_SHOW goes through WinForms' WM_SHOWWINDOW handling, which keeps
    `Visible` in step and raises VisibleChanged (ambient animation stop / wake only).
  - `--background` start: the handle is created hidden (SetVisibleCore keeps it hidden), and hidden
    windows have no button, so nothing flashes. Hide to tray (HideForLater, Esc, ×, Alt+F4) removes the
    button; showing brings it back (normal Windows behaviour).
  - Demo: the stored value is used only when `--settings=` was passed; plain `--demo` keeps no button.
  - `UpdateDockState` returns early while the window is minimized, so docking does not move a
    minimized window; the 500 ms watcher re-docks after restore.
- Taskbar entry shows the embedded app icon (WM_GETICON big/small set by WinForms from `Icon`) and the
  title "OpenVPN Hızlı Arama". The borderless window has no WS_MINIMIZEBOX, so clicking its button
  activates it but does not minimize it; hiding stays Esc / × / hotkey / tray (README says so).

## Verification

- `tests\run-tests.ps1`: 431 passed, 0 failed (399 before + 32 new: setting default / round trip /
  Clone / Normalize / old file without the key / wrong types; Wanted, ExStyle, Plan, Apply on a null
  handle; a real hidden WinForms form flipped 4 times keeps the same handle and is not recreating).
- Scratch build into the session scratchpad (csc line from build.ps1 with /win32icon and the app.ico
  and SVG resources), run only with `--demo --instance=tbtest* --settings=<scratch json>`, killed by PID.
  Window found by PID and title; taskbar checked through UI Automation (Taskbar.TaskListButton peers):
  - setting off: APPWINDOW=False, owned=True, taskbar button: none.
  - setting on: APPWINDOW=True, TOOLWINDOW=False, big+small icon set, button
    "OpenVPN Hızlı Arama - 1 çalışan pencere".
  - flip off then on with the same calls as TaskbarButton.Apply (done from the harness process):
    same hwnd both times, window visible, button gone then back; hidden window: no button.
  - `--background` with the setting on: window hidden, APPWINDOW=True, no button (no flash).
  - style 0x16010000: no WS_MINIMIZEBOX / WS_SYSMENU.

## Not verified / concerns

- The live path through the Settings UI (Kaydet -> ApplyTaskbarButton) was not clicked through (the
  custom-drawn settings controls are not scriptable); the same native calls were exercised from the
  harness and the decision logic is unit tested.
- Ctrl+Alt+V could not be tested in the scratch run because the user's running app owns the hotkey;
  it is argued from the code (handle never changes, RegisterHotKey stays bound).
- When OpenVPN reappears while the panel is minimized, the existing `ShowAndFocus(false)` sets
  `WindowState = Normal`, which can activate the panel. Pre-existing path; minimizing is unusual since
  the window has no minimize box.
- `modules\Glass.cs` was already LF in the working tree and index (HEAD); the edit kept LF, git
  normalizes to CRLF on checkout. New `docs\RELEASE-NOTES.md` was written with LF (other .md files have
  CRLF in the working tree, no BOM); `.gitattributes` `*.md eol=crlf` converts it on checkout.
- No `v2.0.0` tag exists; the release list was built from `git log 034a599..main`.
