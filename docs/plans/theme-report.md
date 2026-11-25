# Derin Mavi (ocean) theme: report

Branch `feat/theme-extra` (from `main` e5c7b53). Version not bumped; release note under `## 2.1.2`.

## What changed

- `Themes.Apply("ocean")` sets a fourth, **dark** palette (`IsLight` = false, so DWM dark title bar,
  `Ui.Highlight`, glyph colors and every `IsLight ? … : …` branch behave as in Glass Dark).
- `Themes.Ids` = `dark, light, system, ocean` (tile / wizard order), `IsValid`, `IndexOf`, `FromIndex`;
  `AppSettings.Normalize` accepts `ocean`; unknown values still fall back to `dark`; `system` still maps
  to Glass Dark / Glass Light only.
- Colors that were hard-coded for Glass Dark outside `Themes.Apply` are now tokens set by every theme
  branch (Glass Dark / Light values unchanged): `Ui.GlassFloating`, `Ui.GlassSelected` (Glass.Tint),
  `Ui.WindowTop` / `Ui.WindowBottom` (opaque fallback gradient in Glass.Layers), `Glass.WindowTint`
  now uses `Ui.WindowBg` in dark themes (same value for Glass Dark), `Ui.MenuTint` / `Ui.MenuSolid` /
  `Ui.MenuFrame` (MenuRenderer panel and DWM border).
- Settings > Görünüm: fourth tile "Derin Mavi" (own mini preview palette, `nav-appearance` icon). The
  page is 508 px wide, so four 146 px tiles do not fit in one row: the fourth tile starts a second row
  and the TEMA holder grows by 146 px (the note below moves down). Nothing else on that page or any
  other page moves.
- Setup wizard: 4th segment "Derin Mavi" + `"ocean"` in both mapping arrays (minimal diff for the merge
  with `feat/wizard`).
- Not themed on purpose: app / tray icon art (`IconArt`, brand), avatar hues, Duplicates' own red.

## Measured palette

Reference: the attached 1x capture (591x625, translucent window over a dark background; outside
#0E0E0E). Colors sampled with System.Drawing (`GetPixel`, 3x3 averages, pixel lines across borders,
brightest-pixel scans for text). Only colors were taken from it.

| Measured in the reference | Hex | Used for |
|---|---|---|
| window background, middle / lower | #1F2834 / #1A212C | `Bg` #1A212C; tint tuned (below) |
| window background, header area | #2B3746 | `WindowTop` target |
| row surface (unselected) | #232A37 | `Surface` |
| row border | #2C3541 | `Border` |
| selected row fill | #2E3E52 | `SelectedFill` |
| selected row border | #8BAED9 | `Accent2`, `Highlight` |
| connect button (selected row) | #5980AD | `Brand`, `Accent` |
| icon on accent button | #DBEAFF | (white glyph, unchanged logic) |
| search field fill (middle) | #303A47 | `Field`, `GlassFloating` |
| segmented thumb rim / selected seg top | #4E5E72 / #3B4959 | `BorderStrong` |
| segmented track | #1A222E | (`GlassSecondary` target) |
| primary text | #EDEDED | `Text` |
| secondary text | #CDCDCD | `Muted` |
| faint icon / caption | #94A3B7 | `Faint` |
| status dot green | #65C299 | `Success` |
| connected card fill / border / text | #294043 / #4E776B / #9AD7B7 | via `Success` glow (logic kept) |
| lock icon (amber) | #B7976B | `Gold`, `Warning` |
| not shown: danger | #D88A8E (derived) | `Danger` (rose in the same family, contrast >= 4.1) |

Derived glass bases (the app keeps its alpha logic: window tint 212, primary 168, secondary 118,
floating 224, selected 190, plus specular / sweep layers), tuned so the rendered result lands on the
measured values:

| Token | Hex | Rendered (harness) vs reference |
|---|---|---|
| `WindowBg` (acrylic tint) | #172231 | gap between rows #1E2735 vs #1F2834; lower #171F2E vs #1A212C |
| `WindowTop` / `WindowBottom` (opaque fallback) | #16263A / #162030 | |
| `GlassPrimary` | #263140 | |
| `GlassSecondary` | #192431 | row #212A36 vs #232A37 |
| `GlassFloating` | #303A47 | search middle #303A47 vs #303A47 |
| `GlassSelected` | #293A4C | selected row #304154 vs #2E3E52 |
| `SurfaceHover` | #2B3443 | derived (Glass Dark's surface -> hover step) |
| `MenuTint` / `MenuSolid` / `MenuFrame` | #141A24 @200 / #1C2330 / #3A4656 | derived from the window tint |

## Contrast (WCAG 2.x)

| Color | on Bg #1A212C | on Surface #232A37 | on Selected #2E3E52 | on Field #303A47 |
|---|---|---|---|---|
| Text #EDEDED | 13.8 | 12.3 | 9.3 | 9.9 |
| Muted #CDCDCD | 10.2 | 9.1 | 6.9 | 7.3 |
| Faint #94A3B7 | 6.3 | 5.6 | 4.3 | 4.5 |
| Danger #D88A8E | 6.1 | 5.5 | 4.1 | 4.4 |
| Success #65C299 | 7.5 | 6.7 | 5.1 | 5.3 |
| Gold/Warning #B7976B | 5.9 | 5.3 | 4.0 | 4.2 |

White on Accent (primary button): 4.1 (bold button text, >= 3). Tests enforce text/muted >= 4.5 and
faint / status colors >= 3 on every ocean surface (`Themes.Contrast`, pure helper).

## Verification

- Tests: 531 C# checks (464 before + 67 new: ids / mapping both ways, system follow, palette sanity and
  contrast, settings round trip and old files) + 28 install tests, all passing.
- Pixel identity: an off-screen scratch harness (never shows a window: it calls the controls' paint
  handlers into in-memory bitmaps) rendered Glass Dark and Glass Light before (e5c7b53) and after:
  glass primitives (solid and acrylic paths), segmented control, search field, island, header buttons,
  context menu, dialog, wizard segments and all four Settings pages. Every image is byte-identical
  except Settings > Görünüm, which differs only below the first tile row (new tile, note moved down).
- Visual: the same harness rendered Derin Mavi (mock main panel, Settings pages, menu, dialog) and the
  colors were tuned against the reference values above. Live screenshots were stopped at the user's
  request, so the real compositor acrylic was **not verified visually**: acrylic adds its own
  luminosity, so on a real desktop the window may look slightly lighter/greyer than the harness.

## Known gaps / to look at

- The glass logic (top specular, light sweep, sheen) is kept, so the header area stays lighter than the
  reference (#3D4855 vs #2B3746) and the selected row has a brighter top edge than the flat reference.
- The connected island uses the shared floating glass plus the success glow: green at the left, not a
  fully green card like the reference.
- Look at in the real app: main panel with a connection (`--demo-connected=`), Settings > Görünüm
  (second-row tile), a context menu (acrylic tint + DWM border), a dialog, the wizard's 4-segment
  control width.
