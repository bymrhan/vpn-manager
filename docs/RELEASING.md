# Releasing a new version

The app checks `https://api.github.com/repos/bymrhan/vpn-manager/releases/latest` and only accepts a
release that carries **both** assets below. `install.ps1` downloads the same two files.

| Asset | Content |
| --- | --- |
| `OpenVPN-Hizli-Arama-Kurulum.zip` | the generic installer package (no profiles) |
| `OpenVPN-Hizli-Arama-Kurulum.zip.sha256` | `<sha256>  OpenVPN-Hizli-Arama-Kurulum.zip` + LF (sha256sum format) |

Without the `.sha256` asset, or with a hash that does not match, nothing is installed.
Prereleases and drafts are ignored by the updater.

## Steps

1. **Bump the version** in `modules\AssemblyInfo.cs` (all three attributes), e.g. `2.1.0`:

   ```csharp
   [assembly: AssemblyVersion("2.1.0.0")]
   [assembly: AssemblyFileVersion("2.1.0.0")]
   [assembly: AssemblyInformationalVersion("2.1.0")]
   ```

   The tag must be the same version with a `v` prefix (`v2.1.0`). Versions compare numerically
   (`2.10.0` > `2.9.0`); suffixes such as `-beta` are not offered.

2. **Build the generic package** (runs the tests first):

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -NoProfiles
   ```

   With `-NoProfiles`, `build.ps1` writes `<zip>.sha256` next to the zip and the release copy under
   the asset name to `outputs\release\`:

   ```
   outputs\release\OpenVPN-Hizli-Arama-Kurulum.zip
   outputs\release\OpenVPN-Hizli-Arama-Kurulum.zip.sha256
   ```

   Never publish a package built without `-NoProfiles`: it contains this PC's VPN profiles with
   their private keys. That build is named `outputs\OpenVPN-Hizli-Arama-PROFILLI-PAYLASMAYIN.zip`,
   gets **no** `.sha256` and nothing in `outputs\release\`, and `build.ps1` prints a warning. Upload
   only the two files from `outputs\release\`.

3. **Commit and push** the version bump, then **create the release** with both files:

   ```powershell
   gh release create v2.1.0 `
     ..\..\outputs\release\OpenVPN-Hizli-Arama-Kurulum.zip `
     ..\..\outputs\release\OpenVPN-Hizli-Arama-Kurulum.zip.sha256 `
     --repo bymrhan/vpn-manager --title "v2.1.0" --notes-file notes.md
   ```

   The release notes (`--notes-file`) are shown in the app's update notice (first lines) and on the
   release page. Do not mark the release as a prerelease, or the app will not offer it.

4. **Check** the release: `install.ps1` on a test machine, or "Şimdi denetle" in
   Ayarlar › Hakkında on an older build (it should report "Sürüm 2.1.0 hazır").

## How installed copies update

- Only a copy running from `%LOCALAPPDATA%\Programs\OpenVPN Hizli Arama` updates itself; a copy
  elsewhere only shows the notice with a link to the release page.
- The app downloads the two assets over HTTPS from this repository's release, verifies SHA-256,
  extracts the package to `%TEMP%\VpnManager-Update\…` and starts the package's
  `kur.ps1 -Update` hidden, then exits. `kur.ps1 -Update` waits for the app to exit, backs up the
  files it replaces, copies `uygulama\*` and the uninstaller, restores the backup if a copy fails,
  and starts the app again. Settings, pins, history and profiles are not touched. A log is written to
  `guncelleme.log` in the install folder.
