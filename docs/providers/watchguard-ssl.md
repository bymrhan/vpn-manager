# WatchGuard Mobile VPN with SSL

Findings behind `modules\WatchGuardSsl.cs` (`WatchGuardStore`, `WatchGuardGui`). Client version on the test PC: 2026.4 (file version `2026, 2, 2, 0`). All host names, users and addresses below are fictional.

## Install and processes

- `C:\Program Files (x86)\WatchGuard\WatchGuard Mobile VPN with SSL\`: `wgsslvpnc.exe` (32-bit native WTL/ATL GUI, tray app), `wgsslvpnsrc.exe` (service "WatchGuard SSLVPN Service", always running), bundled `openvpn.exe` 2.4.x (started by the service while a tunnel is up), `tap0901` driver, language DLLs `wgsslvpncENU.dll`, `FRA`, `JPN`.
- Only one connection at a time. Exiting the GUI closes the connection ("There are active connections that will be close if you exit").
- Single instance: a second `wgsslvpnc.exe` hands its command line to the running one through shared memory ("is already running. Passing command line to process").

## Command line

Strings in `wgsslvpnc.exe` show only two switches:

| Switch | Meaning |
| --- | --- |
| `/noconnect` | Start in the tray only. Used by the client itself for "Launch program on startup" (`"%s" /noconnect` in `HKCU\...\Run`). **Verified**: starting `wgsslvpnc.exe /noconnect` opens no window and starts no `openvpn.exe`. |
| `/Uninstall` | Uninstall helper. |

There is no connect / disconnect / server / user switch. Starting it without `/noconnect` (Start menu shortcut) was not tried: with `RememberMe` and `AutoReconnect` set it could connect on its own.

## Registry (`HKCU\Software\WatchGuard\SSLVPNClient\Settings`)

| Value | Type | Notes |
| --- | --- | --- |
| `Server` | REG_SZ | `host:port,host:port,` (trailing comma), e.g. `vpn1.example:443,vpn2.example:10443,`. Entries are exactly what the user typed (host names or addresses). |
| `Username` | REG_SZ | Last user name (one for all servers). |
| `RememberMe` | DWORD | `2` seen on the test PC (password remembered). |
| `AutoReconnect` | DWORD | Reconnect when the tunnel drops. |
| `UseSAML` | DWORD | `1`: SAML login in an embedded WebView2; the app then only opens the dialog. |
| `LaunchOnStartup`, `Verbosity`, `reqID` | DWORD | Not used. |

Sibling keys `cert_fingerprint`, `cert_fingerprint2` are not read.

## Windows (live probe with `/noconnect`, and the resource DLL)

Top-level windows of `wgsslvpnc.exe` right after start (verified):

- class `ATL:<address>`, title `WatchGuard Mobile VPN Hidden Window!` (hidden main window; the class name changes per build, the title does not)
- class `TrayNotifyIconClass`, title `CTrayNotifyIcon Helper Window`

Tray menu (menu 201 in `wgsslvpncENU.dll`): `Connect...` 32777, `Status...` 32781, `View Logs...` 32776, `Properties...` 32775, `About...` 32778, `Exit` 32779. The string table holds both `Connect...` and `Disconnect`, so the first item is most likely relabelled while connected.

Login dialog (dialog 129, class `#32770`, standard Win32 controls):

| Id | Control |
| --- | --- |
| 1034 | Server combo box (CBS_DROPDOWN, editable) |
| 1000 | User name edit |
| 1001 | Password edit (ES_PASSWORD) |
| 32785 | "Use SAML Authentication" check box |
| 1042 | "auto reconnect" check box |
| 1018 | "Remember password" check box |
| 1005 | Connect (default button) |
| 2 | Cancel |

"Connecting" status window: dialog 206 (static 1007 "Operation", static 1008 openvpn status text). Others: 226 status (Gateway connected, Port connected, ...), 228/229 password-or-PIN prompts for MFA.

## How the app drives it

- **List**: one profile per `Server` entry, id `watchguard-ssl:<entry>`, name `WatchGuard SSL VPN` (plus ` · <host>` when there are several), user name from `Username`. Only the registry is read.
- **Connect**: start `wgsslvpnc.exe /noconnect` if it is not running, post `WM_COMMAND 32777` to the hidden window (then to the tray helper window) to open the login dialog, park it off-screen as soon as it is shown and give the focus back. Choose the server (`CB_FINDSTRINGEXACT` + `CB_SETCURSEL` + `CBN_SELCHANGE`, else `WM_SETTEXT`), set the user name (`WM_SETTEXT`, read back), set the password from Credential Manager (`OpenVpnQuickSearch:watchguard:<entry>`) if the app has one, then post `WM_COMMAND(1005, BN_CLICKED)` to the dialog. No mouse, no keyboard, no focus change. When no password is available and the dialog's password box is empty, the dialog is shown to the user with the server chosen. After Connect, the status window is kept off-screen; the login dialog coming back or any other message box is shown to the user.
- **Disconnect**: post `WM_COMMAND 32777` (the "Disconnect" item while connected); if the tunnel is still up after 12 s, post `Exit` (32779) and answer the "active connections ... exit" question with Yes. Only a Yes/No box whose text equals string 130 of `wgsslvpncENU/FRA/JPN.dll` (the exit-while-connected question, the same id in all three) is answered; any other box is shown to the user. A login dialog that opens instead is cancelled. Any window still parked when a step times out is moved back on screen.
- **The user's own dialog**: a login dialog already open on screen is not filled, pressed or moved; Connect returns "shown to user".
- **Status**: connected when `wgsslvpnc.exe` runs, the log's last state is connected and the TAP adapter (`TAP-Windows Adapter V9`, not the OpenVPN Connect one) is Up or the WatchGuard `openvpn.exe` runs. The server comes from the log.

## Log (`%APPDATA%\WatchGuard\Mobile VPN\wgsslvpnc.log`)

ANSI text, lines `YYYY-MM-DDTHH:MM:SS.mmm <message>` ending in `\r\r\n`; the file grows to megabytes, so only its tail is read. Markers seen on the test PC:

| Line | Meaning |
| --- | --- |
| `Launching WatchGuard Mobile VPN with SSL client. Version ...` | GUI started (not connected) |
| `Requesting client configuration from vpn2.example:10443` | Connect started; the text after `from` is the `Server` entry verbatim |
| `OVPN:>STATE:<t>,TCP_CONNECT` / `WAIT` / `AUTH` / `GET_CONFIG` / `ASSIGN_IP` / `ADD_ROUTES` | openvpn progress |
| `OVPN:>STATE:<t>,CONNECTED,SUCCESS,<vip>,<remote>,<port>,...` and `SSLVPN Connected` | Connected |
| `OVPN:>STATE:<t>,RECONNECTING,...` | Tunnel dropped, retrying |
| `OVPN:>LOG:...,SIGTERM[hard,] received, process exiting`, `>STATE:<t>,EXITING,SIGTERM` | Tunnel closing |
| `Connection Closed.` | Disconnected |
| `************OnEndSession event*************` | Windows logoff/shutdown |
| `auth failed`, `Failed to connect to server: ...` | Failures |

## Not verified live (no real connection was made)

- That `WM_COMMAND 32777` to the hidden window opens the login dialog (and not the tray helper window).
- The combo item format (`host:port` or `host`); both are tried and the text is read back.
- Whether a remembered password shows up in the password box (`WM_GETTEXTLENGTH` > 0); if not, the app asks the user even when the client remembers it.
- That 32777 disconnects while connected; the Exit fallback covers it.
- Behaviour of the dialog after Connect is pressed while it is off-screen.
