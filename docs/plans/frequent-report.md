# Sık kullanılanlar (frequently used profiles): report

Branch `feat/frequent` (worktree `wt-frequent`, from main e5c7b53). Version not bumped.

## Commits
- f4268bc List: "SIK KULLANILANLAR" group under the recent profiles
- 5ac8d70 Settings: shorter description for the frequent toggle (no ellipsis)
- 0d185f3 Docs: frequently used profiles group (2.1.2 notes, README)
- (this report)

## What changed
- `modules/Frequent.cs` (new, pure): `FrequentProfiles.Count(entries, nowUtc)` gives per-id `FrequentStat` (Count, LastStartUtc). It counts only sessions that started in the last 60 days and lasted at least 3 s. Rows with the same id and the same start second count once. `CountOf(stats, id)` gives one profile's count (for tooltips or search). `Pick(pool, stats, shownAbove)` keeps profiles with 3 or more connections that are not already shown above, sorts by count desc, then last start desc, then id, and takes at most 5.
- `modules/Connection.cs`: `ConnectionHistory.LoadCached()` reuses the parsed list while history.csv keeps the same write time and size. The Forti/ext `LastStarts` paths now use it too, so a rebuild does at most one read, and only after the file changed.
- `OpenVpnQuickSearch.cs`:
  - `ApplyFilter` adds `SIK KULLANILANLAR` (Glyph.Refresh) right after `SON KULLANILAN`. Its pool is the same segment-filtered pool, and it excludes live, pinned and recent rows (priority: connected > pinned > recent > frequent > rest). The rows it shows are removed from the OpenVPN/provider/`TÜM PROFİLLER` groups, and the group is hidden when empty. The search branch is unchanged.
  - `RefreshFrequent()` recounts only when the cached history list changed or an hour has passed (the window slides). It runs in `ReloadProfiles` and in `CheckConfigChanged` (2 s tick: one file stat), so the list updates after a session ends even when config.json did not change.
  - Kaydet with a changed `ShowFrequent` rebuilds the list with no restart.
- `modules/Settings.cs`: `ShowFrequent` (default true; a missing or wrongly typed key means true). `SettingsWindow.cs`: toggle row "Sık kullanılanları göster" on Genel (after Sunucu erişim testi); IsDirty updated.
- `modules/Demo.cs`: a 7th row column (fictional session count) and `Demo.History()`. The demo group shows Initech, Stark, Wayne Makine (Forti), Woodgrove and Lucerne (WatchGuard). A few demo rows got old LastConnected values (6 to 14 days) for consistency; the recent top 5 is unchanged.
- Docs: README (Turkish features plus settings plus demo; English summary) and RELEASE-NOTES `## 2.1.2` (single bullet).

## Tests
`tests\run-tests.ps1`: 0 failures. Output has 488 PASS lines (was 464, so 24 new checks) and install tests report 28 passed. The new checks cover: the window, duplicates, short sessions, empty ids, ordering and ties, max 5, min count, dedupe against connected/pinned/recent, empty group, Forti and WatchGuard segments and ids, a deleted profile, settings default/round trip/back-compat/wrong type, and demo history.

## Visual check (partial)
A scratch build (`%TEMP%\freqbuild`, `--demo --instance=freqtest --settings=<scratch>`) was captured over a gradient backdrop in both themes:
- The group sits under SON KULLANILAN with the same header spacing and row style.
- Keyboard Down moves into the group and scrolls.
- `ShowFrequent:false` hides the group.
- `--demo-search=ma` shows the unchanged "N SONUÇ" list.
- The settings row looks correct in the dark and light themes. Its first description was cut off with an ellipsis; the text was shortened (5ac8d70). The shortened text was captured once in light theme and was not re-checked after that.

The coordinator then stopped all GUI work because the windows blocked the user. All scratch processes were killed by PID. **Not verified visually:**
- the segment tabs (posted clicks did not switch them; the unit tests cover segment filtering);
- the toggle followed by Kaydet applying live;
- a `--demo-connected=` run.

What to look at: the FortiClient/WatchGuard tabs (the group shows only that provider), the Genel toggle plus Kaydet (the group appears or disappears without a restart), and the dark theme settings row.

## Notes / concerns
- List section headers do not draw their `Glyph` (`PaintHeader` draws text only), so the glyph choice (Refresh) is metadata only. No new glyph or SVG was added.
- Rows have no free secondary slot (the status slot shows the last-connected time), so the count is not shown in rows. `FrequentProfiles.CountOf` is there for a later tooltip.
- The existing recent header is "SON KULLANILAN" (not "SON BAĞLANILANLAR"); left unchanged.
- OpenVPN rows are counted by history ProfileId, assumed equal to the OpenVPN profile id (as the monitor writes it). Forti/Sophos/WatchGuard use their provider ids.
- Commit trailer is the requested `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. The session's system attribution said Opus 5.5; the explicit task line was used.
