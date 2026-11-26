# Updater: visible, robust update flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make "Şimdi güncelle" show real progress, survive network hiccups, explain every wait or failure, and confirm success after the restart.

**Architecture:** Pure stage/text logic goes into `UpdateLogic` (unit-tested). `UpdateClient.Download` streams to disk with a progress callback, cancel flag and retries. The existing `UpdateNotice` window gains progress / waiting / failed / restarting states and is driven by `SearchForm`'s update methods. `kur.ps1 -Update` writes a success marker the next start turns into a toast.

**Tech Stack:** C# (.NET Framework 4.x, WinForms/GDI+), PowerShell 5.1 helper `paket\kur.ps1`, repo test runner `tests\run-tests.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-26-updater-design.md`

## Global Constraints

- HTTPS only, release assets of `bymrhan/vpn-manager` only, SHA-256 mandatory before anything from the package runs (existing `UpdateLogic.IsAllowedAssetUrl`, `UpdateClient.VerifyAndExtract` stay as the gate).
- C# syntax the repo's `csc` (v4.0.30319) accepts: no `?.`, no string interpolation, no `nameof`, no expression-bodied members.
- UI text Turkish, code comments English. Tests print `PASS`/`FAIL` via `Check(bool, string)` in `tests\Tests.cs`; no network in tests.
- Auto mode (`settings.UpdateMode == "auto"`) installs silently when idle: it must not open the progress window.
- A failed update must leave the old app working (kur.ps1 rollback unchanged).
- Version bump in `modules\AssemblyInfo.cs` only in the release task.

## Review Focus

- Download interrupted mid-stream (connection reset after 60 %): retried up to 3 times, then Failed with a reason, partial file deleted.
- User presses Cancel during download: no error toast, window returns to Offer, no temp folder left.
- Server sends no `Content-Length`: progress shows bytes without percent, never divides by zero.
- Zip larger than `MaxZipBytes` or SHA-256 mismatch: Failed state with the existing reason text, nothing extracted.
- "Şimdi güncelle" pressed while a connection is in flight or a dialog is open: window shows Waiting with the reason and installs when it clears, not silence.
- Pressing "Şimdi güncelle" twice, or the check timer firing during a download: no second download.

---

### Task 1: Stage model and progress text (pure logic)

**Files:**
- Modify: `modules/Updater.cs` (add to `UpdateLogic`, add enum `UpdateStage`)
- Test: `tests/Tests.cs`

**Interfaces:**
- Produces:
  - `internal enum UpdateStage { Offer, Downloading, Verifying, Preparing, Waiting, Restarting, Failed }`
  - `UpdateLogic.FormatBytes(long bytes) -> string` (`"512 B"`, `"4,2 MB"` with `tr-TR` decimal comma, `"1,3 GB"` never needed)
  - `UpdateLogic.StageText(UpdateStage stage, long done, long total, string version) -> string`
  - `UpdateLogic.Percent(long done, long total) -> int` (0..100; `-1` when `total <= 0`)

- [ ] **Step 1: Write failing tests** `UpdateStageText` registered as `Run("Güncelleme aşama metni", UpdateStageText);` asserting:
  - `FormatBytes(512) == "512 B"`, `FormatBytes(4404019) == "4,2 MB"`, `FormatBytes(0) == "0 B"`
  - `Percent(50, 200) == 25`, `Percent(5, 0) == -1`, `Percent(300, 200) == 100`
  - `StageText(UpdateStage.Downloading, 4404019, 9437184, "2.1.7") == "Sürüm 2.1.7 indiriliyor · 4,2 / 9,0 MB · %47"`
  - `StageText(UpdateStage.Downloading, 1000, 0, "2.1.7") == "Sürüm 2.1.7 indiriliyor · 1000 B"` (no total, no percent)
  - `StageText(UpdateStage.Verifying, 0, 0, "2.1.7") == "Doğrulanıyor…"`, `Preparing` -> `"Paket hazırlanıyor…"`, `Restarting` -> `"Yeniden başlatılıyor…"`
- [ ] **Step 2: Run** `powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1` — Expected: compile error (members missing).
- [ ] **Step 3: Implement** the three members and the enum in `modules/Updater.cs`; use `CultureInfo.GetCultureInfo("tr-TR")` and one decimal for MB.
- [ ] **Step 4: Run tests** — Expected: all `PASS`, `0 başarısız`.
- [ ] **Step 5: Commit** `git add modules/Updater.cs tests/Tests.cs && git commit -m "Updater: stage model and progress text"`

### Task 2: Streaming download with progress, cancel and retry

**Files:**
- Modify: `modules/Updater.cs` (`UpdateClient`)
- Test: `tests/Tests.cs`

**Interfaces:**
- Consumes: `UpdateStage` from Task 1.
- Produces:
  - `UpdateClient.CopyWithProgress(Stream source, Stream target, long maxBytes, Action<long> progress, Func<bool> cancelled) -> bool` (returns `false` when cancelled; throws `InvalidOperationException("paket çok büyük")` past `maxBytes`; reports cumulative bytes every 64 KB chunk)
  - `UpdateClient.Download(ReleaseInfo r, Action<UpdateStage, long, long> report, Func<bool> cancelled, out string error) -> string` replacing the 2-argument overload (package folder, or `null` + error; `error == "iptal edildi"` when cancelled). `report(stage, done, total)` is called from the worker thread.
  - Retry rule: a `WebException` or `IOException` during the zip GET retries up to 3 attempts total with 1 s / 3 s waits, each attempt restarting the file; an HTTP 4xx answer is never retried (5xx and network errors are).

- [ ] **Step 1: Write failing tests** `UpdateCopyWithProgress`: a `MemoryStream` of 200 000 bytes copied with `maxBytes` 1 000 000 reports a final cumulative value 200 000 and returns `true`; `cancelled` returning `true` after the first chunk returns `false` and writes fewer than 200 000 bytes; `maxBytes` 100 000 throws `InvalidOperationException`; `progress` values are non-decreasing.
- [ ] **Step 2: Run tests** — Expected: compile error.
- [ ] **Step 3: Implement** `CopyWithProgress` (64 KB buffer) and change `Download` to: GET the `.sha256` (small), open the zip request, read `ContentLength` (may be `-1`: pass `0` as total), `report(Downloading, done, total)`, stream into `File.Create(zip)` via `CopyWithProgress`, then `report(Verifying, 0, 0)` before `VerifyAndExtract`, then `report(Preparing, 0, 0)` around the extract. Keep `IsAllowedAssetUrl` checks, the final-hop HTTPS check from `Get` and `TryDelete(work)` on every failure and on cancel. Update the single caller in `OpenVpnQuickSearch.cs` (`StartUpdateDownload`) to pass `null, () => false` for now so the build stays green.
- [ ] **Step 4: Run tests** — Expected: all `PASS`.
- [ ] **Step 5: Commit** `git commit -am "Updater: streaming download with progress, cancel and retry"`

### Task 3: Progress states in UpdateNotice

**Files:**
- Modify: `modules/Updater.cs` (`UpdateNotice`)

**Interfaces:**
- Consumes: `UpdateStage`, `UpdateLogic.StageText`, `UpdateLogic.Percent`.
- Produces on `UpdateNotice`:
  - `void SetStage(UpdateStage stage, long done, long total, string version)` (UI thread; repaints; Offer buttons hidden in other stages)
  - `void SetWaiting(string reason)`, `void SetFailed(string reason)`
  - `event Action Cancel, Retry` (Cancel shown only while `Downloading`; Retry and "Sürüm sayfasını aç" (existing `Notes` event) shown in `Failed`)
  - Constructor and existing events `UpdateNow, Later, Skip, Notes` unchanged.

- [ ] **Step 1: Implement** a progress bar (rounded glass track, `Ui.Highlight` fill, indeterminate sliding segment when `Percent == -1` or stage is Verifying / Preparing / Restarting, animated by a 30 ms `Timer` only while visible) under the caption; caption/body switch per stage using `StageText`; the window must not close on the primary button in Working states (today `primary.Click` closes it: change so `UpdateNow` hides Offer buttons and keeps the form open).
- [ ] **Step 2: Build** with `powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1` — Expected: compiles, tests still pass.
- [ ] **Step 3: Commit** `git commit -am "Updater: progress, waiting and failed states in the notice"`

### Task 4: Wire the flow in SearchForm (waiting reasons, cancel, retry, restart gap)

**Files:**
- Modify: `OpenVpnQuickSearch.cs` (`StartUpdateDownload`, `InstallPending`, `InstallUpdate`, `ShowUpdateNotice`, ~lines 1960-2060)
- Test: `tests/Tests.cs`
- Modify: `modules/Updater.cs` (`UpdateLogic`)

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: `UpdateLogic.WaitReason(bool busy, bool dialogOpen) -> string` (`null` when neither; `"Bağlantı işlemi bitince kurulacak"` for busy; `"Açık pencereler kapanınca kurulacak"` for dialog; busy wins).

- [ ] **Step 1: Write failing test** `UpdateWaitReason` for the four combinations above.
- [ ] **Step 2: Run tests** — Expected: FAIL/compile error; then implement `WaitReason` in `UpdateLogic`.
- [ ] **Step 3: Rewire** (one line of approach each):
  - `StartUpdateDownload(r, userAsked)`: keep the `updateDownloading` guard (covers double press and timer); when `userAsked`, the `UpdateNotice` stays open and receives `SetStage` from `report` through `UiInvoke`; auto mode passes a `report` that only logs.
  - Cancel event sets a `volatile bool updateCancel` read by the `cancelled` callback; on `error == "iptal edildi"` return the notice to Offer, no toast, no `updateBadVersion`.
  - Retry event calls `StartUpdateDownload(r, true)` again.
  - `InstallPending` user path: when `UpdateBusy || UpdateDialogOpen`, call `notice.SetWaiting(UpdateLogic.WaitReason(...))` instead of only `ReportUpdate`.
  - `InstallUpdate`: call `notice.SetStage(UpdateStage.Restarting, 0, 0, version)` before starting the helper; on `StartInstall` failure call `notice.SetFailed(err)`; on success keep the window up for 1.2 s (`Timer`) so the user sees "Yeniden başlatılıyor…" before `ExitApp()`.
  - A failed download calls `notice.SetFailed(err)` in addition to the existing log and `ReportUpdate`.
  - Only the notice-less auto path stays silent.
- [ ] **Step 4: Run tests** — Expected: `PASS`, `0 başarısız`.
- [ ] **Step 5: Commit** `git commit -am "Updater: drive the notice from the update flow, waiting reasons, cancel and retry"`

### Task 5: Success marker and "güncellendi" message

**Files:**
- Modify: `paket/kur.ps1` (success path of the `-Update` branch)
- Modify: `modules/Updater.cs` (`UpdateClient`, `UpdateLogic`)
- Modify: `OpenVpnQuickSearch.cs` (startup after the window exists)
- Test: `tests/Tests.cs`, `tests/install-update.tests.ps1`

**Interfaces:**
- Produces:
  - `UpdateClient.UpdatedMarkerName = "guncellendi.txt"`
  - `UpdateLogic.ParseUpdatedMarker(string text, out string version) -> bool` (first line is the version via `TryParseVersion`, second line a UTC ISO time within the last 24 h counted by the caller; returns `false` for garbage)
  - `UpdateClient.TakeUpdatedMarker(string installDir) -> string` (version or `null`; deletes the file when read)

- [ ] **Step 1: Write failing tests**: `ParseUpdatedMarker("2.1.7\n2026-11-26T10:00:00Z", out v)` is `true` with `v == "2.1.7"`; `"abc"` and empty are `false`; `TakeUpdatedMarker` on a temp dir returns the version once and `null` the second time. In `install-update.tests.ps1` add a case asserting that after a successful `-Update` run `guncellendi.txt` exists with the version on line 1 (follow that file's existing harness for the success case).
- [ ] **Step 2: Run** `tests\run-tests.ps1` — Expected: FAIL.
- [ ] **Step 3: Implement** the marker write in `kur.ps1` right where it deletes `guncelleme-basarisiz.txt` on success (version + `(Get-Date).ToUniversalTime().ToString('o')`), the two C# members, and in `SearchForm` after first show: `var v = UpdateClient.TakeUpdatedMarker(UpdateLogic.InstallDir); if (v != null) SetStatus("Sürüm " + v + "'e güncellendi");` (installed copy only).
- [ ] **Step 4: Run tests** — Expected: all `PASS`, install tests `passed`.
- [ ] **Step 5: Commit** `git commit -am "Updater: success marker and post-restart message"`

### Task 6: Demo simulation, visual check, release

**Files:**
- Modify: `modules/Demo.cs`, `OpenVpnQuickSearch.cs` (`Main` switch, `SearchForm` demo hook)
- Modify: `modules/AssemblyInfo.cs`, `docs/RELEASE-NOTES.md`

**Interfaces:**
- Produces: `Demo.UpdateSim` (bool, set by `--demo-update`): shows an Offer notice for a fake `ReleaseInfo` and, on "Şimdi güncelle", plays Downloading 0-100 % over 6 s, Verifying, Preparing, Restarting, then returns to Offer without downloading or installing anything; a second switch value `--demo-update=fail` ends in Failed.

- [ ] **Step 1: Implement** the simulation (no network, no file writes) and build a test exe as in earlier sessions (`csc` with all `modules\*.cs` and `assets\*`).
- [ ] **Step 2: Verify visually**: run `--demo-update --instance=shot --settings=<tmp>.json`, capture the notice window in each stage (Offer, Downloading ~50 %, Verifying, Restarting, Failed) with the PrintWindow harness, read the PNGs; check that text fits at 100 % and the bar animates. Expected: each stage readable, no clipped text.
- [ ] **Step 3: Full verification**: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -NoProfiles` — Expected: `0 başarısız`, package and `.sha256` in `outputs\release\`.
- [ ] **Step 4: Release**: bump `AssemblyInfo.cs` to the next patch version, add a `## <version>` section to `docs/RELEASE-NOTES.md` describing progress, cancel/retry, waiting reasons and the post-update message, commit, push, `gh release create v<version>` with both assets per `docs/RELEASING.md`.
- [ ] **Step 5: Commit** `git commit -am "Updater: demo simulation, release notes"`

## Self-review

- Spec coverage: progress stages (T1, T3), cancel + retry (T2, T4), failure state (T3, T4), waiting reason (T4), post-restart message (T5), demo/visual check (T6). Security model untouched (T2 keeps the gates).
- Type consistency: `UpdateStage`, `StageText`, `Percent`, `Download(r, report, cancelled, out error)`, `SetStage/SetWaiting/SetFailed`, `WaitReason`, `TakeUpdatedMarker` are used with identical names across tasks.
- Proportion: six tasks for roughly 400-600 lines of change; bodies are left to the implementer.
