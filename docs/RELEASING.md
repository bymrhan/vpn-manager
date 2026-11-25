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

## Issue token for direct error reports (optional)

With a token embedded, the app creates the (public) `hata` issue itself after the user agreed
(modules\IssueSender.cs). Without one the build is unchanged and reports open in the browser.

Anyone can extract the token from the exe, so choose the token by what it can do.

**Recommended: a bot account's classic token (GitHub UI only, cannot be automated)**

1. Create a separate GitHub account for the app (e.g. `vpn-manager-reports`, with its own e-mail).
   It must own no repositories and must **not** be a collaborator on `bymrhan/vpn-manager`.
2. Signed in as that account: *Settings* > *Developer settings* > *Personal access tokens* >
   *Tokens (classic)* > *Generate new token (classic)*. Scope: **`public_repo` only**. Expiration:
   at most 1 year.
3. Save the token as the first line of `secrets\issue-token.txt` in the repository folder
   (`secrets/` is in `.gitignore`), or set `$env:VPNM_ISSUE_TOKEN` for the build shell only.

On this public repository the bot can only do what any stranger can: open issues and write
comments (it could also comment on or star other public repositories, so keep the account empty).
Worst case if extracted: spam under the bot's name; block the account (repository *Settings* >
*Moderation* or `gh api -X PUT /user/blocks/<bot>` as the owner) and revoke the token.
Because the bot cannot set labels, GitHub silently drops `labels: ["hata"]`: its issues arrive
**without** the `hata` label. Label them during triage (docs\TRIAGE.md), or add a small Action that
labels issues whose title starts with `[hata] `; the app's duplicate check only matches issues that
carry `hata`, so unlabelled reports are not commented on until they are labelled.

**Alternative (not recommended): the owner's fine-grained token.** *Fine-grained tokens* >
*Generate new token*, *Repository access* = **Only select repositories** > `bymrhan/vpn-manager`,
*Permissions* = **Issues: Read and write** only, expiration at most 1 year. It sets the `hata` label,
but if extracted it can also edit, close, reopen, lock and relabel **any** issue and edit or delete
comments on this repository, and every action appears as the maintainer.

Make sure the `hata` label exists (`gh label create hata --color d73a4a`); a label error (422) makes
the app retry once without a label.

**Build**: `build.ps1 -NoProfiles` prints `token embedded: yes` (never the value). It refuses to run
when anything under `secrets\` is tracked by git. It builds the exe, calls the exe's own
`IssueToken.Encode` (XOR with a fixed key + base64) and builds again with the resource `issue.token`;
the encoded file lives in its own temp folder (never in the zipped staging folder) and is deleted.
The encoding only avoids trivial string scanning, it is not protection.

**Never commit it.** GitHub secret scanning revokes tokens it finds committed as text in public
repositories, so a token that reaches the repository (or an issue / log pasted somewhere public)
stops working, and every installed copy falls back to the browser flow. Keep it only in `secrets\`.

**Rotate** before the expiry date: create a new token, put it in `secrets\issue-token.txt`, ship a new
release, then delete the old token. Copies still running with an expired token get 401, stop
sending for 24 h at a time and use the browser flow.

**Abuse** (spam made with an extracted token): revoke the token at once on the page where it was
created (all installed copies fall back to the browser flow), block the bot account, close / lock /
delete the spam (`gh issue list --author <bot> --state all`, `gh issue close <n>`, `gh issue lock <n>`,
`gh issue delete <n>`), then ship a
release with a new token. Each install is limited to 3 issues or comments a day, but an extracted
token is not bound by the app's limits.