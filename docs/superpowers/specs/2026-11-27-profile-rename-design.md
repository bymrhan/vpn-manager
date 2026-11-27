# Renaming profiles from the panel, written back to each VPN client (design)

## Intent and why

WatchGuard SSL VPN keeps only a server list, so its rows are named "WatchGuard SSL VPN · <ip>" and
cannot be told apart by customer. The user wants to give profiles a proper name from inside the panel.
For the clients that do have their own profile name (OpenVPN Connect, FortiClient, Sophos Connect) the
new name must also be written into that client, so the two programs never show different names
("tek taraflı olmasın").

## Goals

- "Yeniden adlandır…" for every VPN row: row menu item and `F2` on the selected row.
- WatchGuard: the name is stored by the panel (the client has no place for it).
- OpenVPN Connect, FortiClient, Sophos Connect: the name is changed in the client's own store; the panel
  keeps reading names from the client, so a rename made inside the client shows up in the panel too.
- Pins, connection history, saved passwords and "last connected" survive a rename.
- A failed write-back is reported to the user with the reason; the panel never shows a name the client
  does not have (except while an OpenVPN rename is waiting, see below).

## Non-goals

- Brave 365 profiles (not VPN profiles; named in Brave).
- FortiClient IPsec tunnels (`forti-ipsec:`): rename is offered for SSL tunnels only in this version.
- Renaming several profiles at once.
- Changing host, user name or any other profile field.

## Anti-goals

- Dropping an active VPN connection because of a rename.
- Leaving OpenVPN Connect's `config.json` damaged or half written.
- A silent mismatch: the panel showing the new name while the client still has the old one, with no
  indication.

## Constraints found in the code and on this PC

- OpenVPN Connect 3.9 has no rename command (`ImportService.RenameProfile` is a stub that always fails).
  Names live in `%APPDATA%\OpenVPN Connect\config.json`: `persist:root` is a JSON string, its `status` is
  again a JSON string, `status.profiles.<id>` has `profileDisplayName`, `profileName` and `name`
  (`name` also contains the host). `status.connectedProfile` / `previouslyConnectedProfile` repeat
  `profileName` / `profileDisplayName`. OpenVPN Connect rewrites the whole file from memory on almost
  every action, so an edit made while it runs is lost. `ProfileSources.NameKeys` reads
  `profileDisplayName` first.
- FortiClient: the tunnel name is the registry key name under
  `HKLM\SOFTWARE\Fortinet\FortiClient\Sslvpn\Tunnels` (not writable without elevation) and under
  `HKCU\Software\Fortinet\FortiClient\Sslvpn\Tunnels`. The panel's profile id is `forti-ssl:<name>`, and
  the saved password (`FortiCredentialStore`) is keyed by the tunnel name.
- Sophos Connect: `sccli update -n NAME -y DISPLAYNAME` sets a display name; the internal name (the
  panel's id `sophos:<name>`) does not change. The panel currently shows the internal name from
  `sccli list`.
- WatchGuard: id is `watchguard-ssl:<host:port>`; the name is built in `WatchGuardStore` ("WatchGuard
  SSL VPN" or "WatchGuard SSL VPN · host").

## Approaches

1. **Client is the source of truth (chosen by the user).** A rename is written into the client's store;
   the panel reads names from there. A panel-side name store exists only for WatchGuard. One name on
   both sides; client-side renames appear in the panel. Cost: one write path and its failure cases per
   client, a UAC prompt for FortiClient, an OpenVPN Connect restart.
2. **Panel alias is the source of truth.** Every profile gets a panel-side name that always wins in the
   list; writing to the client is best effort. Rename always works at once, but a failed write-back
   leaves two different names unnoticed and client-side renames are hidden. Rejected: this is the
   one-sided behaviour the user wants to avoid.
3. **Alias only, no write-back.** Rejected: does not meet the request.

For OpenVPN the user accepted that OpenVPN Connect is closed and reopened to apply a rename. Removing
and re-importing the profile under a new name was rejected: the profile id changes and the saved
password is lost.

## Design

### UI

- Row menu: "Yeniden adlandır…" after "Sabitle", shortcut text `F2`. `F2` in `OnFormKeyDown` renames
  the selected row. Not shown for Brave rows, FortiClient IPsec rows, or in demo mode.
- Dialog: the existing glass dialog style, title "Yeniden adlandır", one text box pre-filled with the
  current name and selected, buttons "Kaydet" / "Vazgeç", `Enter` / `Esc`. Under the box one muted line
  saying what will happen, per provider:
  - WatchGuard: "Ad yalnız VPN Manager'da saklanır."
  - OpenVPN, not connected: "OpenVPN Connect kapatılıp yeniden açılacak."
  - OpenVPN, a tunnel is up or a connect/disconnect is in flight: "OpenVPN bağlantısı kesilince
    uygulanacak."
  - FortiClient: "Windows yönetici onayı isteyecek."
  - Sophos: "Sophos Connect'te görünen ad değişir."
- Validation (pure function, tested): trimmed; 1-64 characters; no control characters; for FortiClient
  additionally none of `\ / : * ? " < > |` (registry key name and credential target); not equal
  (case-insensitive, Turkish culture) to another profile's name of the same provider. Unchanged name
  closes the dialog without doing anything. The reason is shown under the box and "Kaydet" stays
  disabled while invalid.
- WatchGuard only: an empty box is allowed and means "remove my name" (back to the automatic one); the
  hint line says so.
- Result: status toast `"<old>" → "<new>" olarak değiştirildi`; on failure a glass dialog with the
  reason. The list is rebuilt keeping the row selected.

### Panel-side names (WatchGuard)

- New store `%APPDATA%\OpenVpnQuickSearch\names.json`: `{ "<profile id>": "<name>" }`, UTF-8, written
  through a temp file and replace. A broken or missing file means "no names" and never breaks the list.
- Applied in one place after profiles are loaded (next to `VpnProviders.ApplyLastConnected`): for a
  WatchGuard profile with an entry, `Name` is replaced and `Initials` / search keys are recomputed.
  The row's second line still shows provider, host and user, so the server stays visible.
- Entries whose profile no longer exists are kept (the server may come back); the file is tiny.
- The backup (`BackupService`) includes `names.json`; restore puts it back. (Builder: follow how pins
  are carried in the manifest.)

### OpenVPN Connect

- New `OpenVpnRename` module. `Apply(id, newName)`:
  1. Refuse unless no tunnel is up (`ConnectionMonitor.Current.Connected == false`) and no connect /
     disconnect is in flight.
  2. Close OpenVPN Connect: ask its windows to close (`CloseMainWindow`), wait up to 5 s for all
     `OpenVPNConnect` processes to exit, then kill what is left. Remember whether a window was showing.
  3. Read `config.json`, copy it to `config.json.vpnm-yedek` (one backup, overwritten each time).
  4. Edit in memory, preserving the double string encoding and every other key: in
     `status.profiles.<id>` set `profileDisplayName` and `profileName` to the new name, and in `name`
     replace the old display name with the new one when it occurs there (otherwise leave `name` alone).
     Do the same for `connectedProfile` / `previouslyConnectedProfile` when their `id` matches.
  5. Write to a temp file in the same folder, re-read and parse it, check the profile count is unchanged
     and the name is the new one, then replace `config.json`. Any failure: restore the backup.
  6. Start OpenVPN Connect again (minimized through `QuietWindow.KeepProcessInBackground` when no
     window was showing before).
  The JSON transformation (step 4) is a pure function `string Rewrite(string json, string id, string
  newName)` and carries the tests.
- Deferred renames: when step 1 refuses, the rename is put in `pending-renames.json`
  (`{ "<id>": "<new name>" }`, same folder as `names.json`). The list shows the new name at once for
  that profile with a small clock mark and the tooltip "OpenVPN'e yazılmayı bekliyor". When the monitor
  reports no tunnel and nothing in flight (checked on the connection-changed event and at start), all
  pending renames are applied in one close / edit / reopen cycle and the file is cleared. A pending
  entry whose profile is gone, or whose client name already equals it, is dropped.
- Open question for the builder, to verify on the real client before finishing: OpenVPN Connect keeps
  the edited `profileDisplayName` after its next own save, and whether `name` must change too. If the
  client discards the edit, stop and report: the approach for OpenVPN has to be reconsidered with the
  user.

### Sophos Connect

- `sccli update -n <internal name> -y <new name>` through the existing hidden-process runner (10 s
  timeout, fixed error text with exit code).
- The panel must show the display name: read it where the connection details are already read
  (`sccli get`), fall back to the internal name when there is none. Id, pins, history and saved
  password stay on the internal name, so nothing has to be moved.
- Builder: confirm the key under which `sccli get` returns the display name; if it returns none, keep
  the new name in `names.json` as well so the panel shows it (still written to the client).

### FortiClient (SSL tunnels)

- Refuse while this tunnel is connected, connecting or disconnecting ("Önce bağlantıyı kesin").
- The registry work needs elevation: the panel starts its own exe with `--rename-forti` and two
  base64 arguments (old name, new name) through `runas` (one UAC prompt), waits for the exit code.
  Elevated part: for HKLM and HKCU tunnel keys, copy the key (all values, value kinds preserved) to the
  new name, verify, delete the old key; if the new name already exists anywhere, change nothing and
  return an error code. Also update the HKCU selection value (`SelectionKey` "connection") when it
  names the old tunnel. Exit codes map to fixed Turkish messages; UAC declined = "Yönetici onayı
  verilmedi; ad değişmedi".
- After success, in the panel process: move the saved password to the new tunnel name
  (`FortiCredentialStore`), replace the id in pins (`PinStore`), rewrite `ProfileId` /`ProfileName` in
  `history.csv` for that id, move the reachability / last-connected state keyed by id, then reload.
- The key copy / plan logic is a function over an abstract key (so it is tested against a scratch HKCU
  key); the elevated entry point only wires it to the real hives.
- Builder: check whether a running FortiClient console shows the new name without a restart; if not,
  restart only the console (not the service) when it was running and no tunnel is up.

## Error handling

Every write path returns `null` or one fixed Turkish message; nothing from the clients' own output is
shown or logged (profile and server names stay out of `AppLog`, as today). `AppLog` gets one line per
rename: provider, outcome, and for failures the step that failed.

## Testing

Unit tests in `tests\Tests.cs`: name validation; `names.json` round trip and broken file;
`OpenVpnRename.Rewrite` on a fixture with the double encoding (other profiles and keys byte-for-byte
unchanged after a parse/compare, name fields changed, unknown id is an error, connectedProfile
updated); pending-rename decisions (apply / keep / drop); Forti key copy on a scratch HKCU key including
the "target exists" refusal; pin / history id migration; Sophos argument building.

Manual check on this PC before release (needs the real clients, done by the user or with their go-ahead):
one rename per provider, and an OpenVPN rename requested while connected.

## Left to the builder

File and class names, dialog layout details, the clock mark's drawing, exact wording of error messages,
and the order of implementation. Suggested order: validation + dialog + WatchGuard names (delivers the
user's first need alone), then Sophos, OpenVPN, FortiClient.
