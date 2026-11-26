# In-app updater: visible, robust update flow (design)

## Intent and why

A user on another PC pressed "Şimdi güncelle" in the update notice. All they saw was a very short
"indiriliyor…" line, then the app looked frozen, then it suddenly restarted. The update worked, but
nothing told the user what was happening, so it felt broken. Goal: the user always knows which stage
the update is in, can cancel while it downloads, gets a clear message and a way forward when it fails,
and sees a confirmation after the restart.

## Root causes found in the code (modules\Updater.cs, OpenVpnQuickSearch.cs ~1881-2070)

1. `UpdateClient.Download` reads the whole zip into memory with `Get(...)` (no streaming, no progress) and
   `SetStatus("Sürüm X indiriliyor…")` is a toast that disappears after 2.6 s (`statusTimer`). Download, SHA-256
   and extraction then run with no UI at all.
2. After verification `StartInstall` starts a hidden PowerShell and waits up to 10 s for `.bekliyor`
   (PowerShell cold start). No UI during that time: the window looks frozen.
3. `ExitApp()` then `kur.ps1 -Update` waits, copies, restarts the app: a gap of several seconds with no
   window and no tray icon, then the app reappears with no explanation.
4. When the app is busy or a dialog is open, "Şimdi güncelle" silently waits (only a text in Settings).
5. A failed download shows only a toast; one network hiccup (10 s read timeout, no retry) ends it.

## Goals

- Persistent, accurate progress: stage names (İndiriliyor / Doğrulanıyor / Hazırlanıyor / Yeniden başlatılıyor),
  bytes and percent while downloading.
- Cancel while downloading. Retry automatically on a transient network error (3 attempts, backoff).
- Clear failure state with the reason and buttons (Tekrar dene, Sürüm sayfasını aç).
- Explain waiting ("bağlantı işlemi bitince kurulacak") instead of silence.
- After the restart, one message: "Sürüm X'e güncellendi".

## Non-goals / anti-goals

- No change to the security model: HTTPS only, repository assets only, mandatory SHA-256 before anything runs.
- No delta updates, no new installer technology, no code signing.
- Do not make auto mode noisier: background auto-updates keep installing silently when idle.
- The update must never leave the user without a working app (existing rollback in kur.ps1 stays).

## Constraints

- .NET Framework 4.x, WinForms/GDI+, single exe, `csc` build (no NuGet). C# 5-compatible syntax as in the repo.
- UI strings are Turkish; code comments English.
- Tests are the repo's own `tests\Tests.cs` (run by `tests\run-tests.ps1`); no network in tests.

## Approaches considered

1. **Only add a toast per stage.** Cheapest, but toasts vanish after 2.6 s: same complaint. Rejected.
2. **Progress panel in the existing UpdateNotice + streaming download (chosen).** The notice already exists,
   stays on screen, matches the glass style; add a progress mode to it and stream the download.
3. **Separate updater process with its own window.** Survives the app exiting, but a second UI to build and
   ship; kur.ps1 stays hidden today for good reasons. Rejected for size.

## Decisions

- `UpdateNotice` gets states: Offer (today), Working(stage, bytes, total), Waiting(reason), Failed(reason),
  Restarting. One window for the whole flow.
- Download streams to a file with a progress callback and a cancellation flag; transient failures retry 3 times.
- `UpdateStage` and text formatting live in `UpdateLogic` (pure, unit-tested).
- A marker file `<InstallDir>\guncellendi.txt` (version, UTC time) is written by `kur.ps1 -Update` on success;
  the app reads and deletes it at start and shows a toast.
- A `--demo-update` switch plays a fake update (no network, nothing installed) so the UI can be screenshotted.

## Left to the builder

Exact pixel layout of the progress bar, easing, and wording beyond the strings listed in the plan.
