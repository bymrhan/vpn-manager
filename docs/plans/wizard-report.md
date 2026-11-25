# Kurulum yardımcısı raporu (feat/wizard)

Branch `feat/wizard` (worktree `wt-wizard`), base `main` e5c7b53. Version not bumped.

## Commits

- `6fb6865` SToggle: fade a disabled toggle into its row
- `a3b8f2c` Setup wizard: detect all four clients, provider toggles, live theme
- `4af114a` Docs: setup wizard scans all four clients, theme applies live

## What changed

- `modules/SetupWizard.cs`
  - New `WizardLogic` (pure): status texts for OpenVPN (with short version) / FortiClient /
    Sophos (`taranıyor…` placeholder, zero-connection hint `.pro/.ovpn`) / WatchGuard (zero-server
    hint), `ToggleOn` / `SavedShow` (toggle defaults and persistence), `ThemeIndex` / `ThemeKey`.
  - Detection card lists all four clients. WatchGuard count = `WatchGuardStore.Load()` (registry
    only). Sophos count = `SophosStore.Load()` on a ThreadPool worker started in `OnShown`, result
    marshalled with `BeginInvoke` (guarded against a closed form); only `sccli list` / `get`.
  - AYARLAR card: FortiClient / Sophos Connect / WatchGuard SSL VPN toggles; on for an installed
    client (first run) or the saved choice; disabled for a missing client, whose saved value is kept.
    Saved to `ShowForti` / `ShowSophos` / `ShowWatchGuard`.
  - Theme: wizard applies the stored theme on start; the segmented control calls `Themes.Apply` +
    `ApplyColors()` (BackColor, page, panels, DWM attributes 20/33/34 via `ApplyFrame`, like
    `SettingsForm`). `SaveBasics` (Atla / Kurulumu tamamla / Esc) applies and saves the theme;
    closing without saving restores the original theme (`FormClosed`).
  - Demo mode: all four clients "installed" with counts from `Demo.Profiles()`; no real reads,
    no imports, no Startup/desktop shortcut (previously the wizard in demo could touch both).
  - Texts: subtitle and desktop shortcut description name the four clients.
  - ProfilePack left OpenVPN/Forti only.
- `OpenVpnQuickSearch.cs`: Program applies the stored theme (not hard-coded dark) before the
  wizard; the existing `Themes.Apply(settings.Theme)` after the wizard covers the main window.
- `modules/SettingsWindow.cs`: `SToggle` draws a faded overlay when disabled (also affects the
  wizard's disabled import toggles; SettingsWindow currently has no disabled toggles).
- `tests/Tests.cs`: `WizardLogicRules` (18 checks).
- `README.md` (TR + English summary + demo note), `docs/RELEASE-NOTES.md` (new `## 2.1.2`, one bullet).

## Verification

- `tests\run-tests.ps1`: 482 C# checks passed, 0 failed (was 464); install tests passed (exit 0).
- Scratch build in the session scratchpad (build.ps1's csc line), run only with
  `--demo --setup --instance=wizscratch --settings=<scratch>\settings.json`, screenshots over a grey
  backdrop: dark start, scrolled options, live switch dark->light and light->dark (stored light
  start), no recolor leftovers, no clipping, Sophos row updated from the worker. Finishing wrote
  `Theme`, `ShowSophos`, `ShowWatchGuard` to the scratch settings file. Scratch processes killed by PID.

## Concerns / not verified

- The disabled-toggle look and the "not installed" texts were not seen on screen (demo marks every
  client installed; a non-demo run would read the real clients, which was avoided).
- Behaviour change: on first run with FortiClient missing, `ShowForti` now stays at its default
  (true) instead of being written false; the main app still gates Forti on installation.
- In the light theme the primary "Kurulumu tamamla" button looks pale; that is the existing
  `SButton` light styling (unchanged here).
- Sophos count via `SophosStore.Load()` runs `get` per connection (10 s timeout each); with many
  slow connections the placeholder may stay for a while (UI never blocks).
