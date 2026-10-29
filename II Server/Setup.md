# Hosting the mirror relay

The relay is the only server-side piece of Infirmary Integrated: II Command posts each room's
current step, and each room PC (II:SIM `--receive`) reads it back every 5 seconds. Everything
else (physiology, waveforms, scenarios) runs in the desktop applications.

**Houston runs it on the OSCEtime relay PC**, `23-ENGM-8Q0XR33` (10.116.17.30), port **8480**,
as its own boot-time scheduled task (**II Mirror Relay**) beside OSCEtime's. It is `mirror_relay.py`: one stdlib
Python file, no database and no web server. The upstream PHP scripts in this folder
(`mirror_get.php`, `mirror_post.php`, `version.php`) are kept for reference only.

## What it does

| Request | From | Reply |
|---|---|---|
| `mirror_post.php?accession&key_access&key_edit&patient&updated` | II Command | `ENTRY_ADDED`, `ENTRY_UPDATED`, or `BAD_PASSWORD` if the edit-password hash doesn't match |
| `mirror_get.php?accession&accesshash` | room PCs | `updated` and `patient` lines, or nothing if the access hash doesn't match |
| `mirror_status.php?accession&key_edit` | II Command | seconds since a room PC last read the room, empty if none has; drives the card's **receiving** / **⚠ room PC silent** indicator |
| `version.php` | (unused) | empty; the fork's update check is off |

The paths and replies are exactly the upstream PHP relay's, so II needs nothing but the
address. `mirror_status.php` is ours; on the upstream relay it is a 404, and the card just shows
`LIVE`.

## Install (once, on the relay PC)

1. Copy `mirror_relay.py`, `install-mirror-relay.ps1` and `Install-Relay.cmd` to a USB drive
   (or any folder). The relay PC already has Python 3.12.
2. On the relay PC, double-click **`Install-Relay.cmd`** and accept the admin prompt. (By hand
   instead: admin PowerShell, `Set-ExecutionPolicy -Scope Process Bypass`, then
   `.\install-mirror-relay.ps1`.)

   It copies the relay to `C:\relay\ii` and registers scheduled task **II Mirror Relay** (runs as SYSTEM at
   boot, restarts a minute after a crash, logs to `C:\relay\logs\IIMirrorRelay.log`). It opens TCP 8480 on
   Domain/Private networks to `10.116.16.0/23` only (`-AllowFrom` to widen), then proves a
   step can be posted and read back. Re-running it is safe; `-Uninstall` removes it.
3. Set `"server": "http://10.116.17.30:8480/"` in `stations.json` on
   `H:\Sim Techs\updates\ii`.
4. Re-run `Install-Station.ps1 -Station SIMn` on each room PC so its launcher carries the new
   address.
5. II Command picks the address up through **+ Add Room**. For blank rooms, also set it in the
   toolbar's server box and save the layout.

Manage it in Task Scheduler as **II Mirror Relay** (or `Restart-ScheduledTask "II Mirror Relay"`). Restarting is harmless: state is
memory only, and II Command re-posts every 5 s.

## Capacity and limits

Twelve rooms is about 2.4 reads a second of a few kilobytes each, so the relay idles. It holds
at most 500 rooms. The client sends the whole encrypted step **in the URL**. Python's
`http.server` caps a request line at 64 KB. A full step is about 2.4 KB, and a unit test
(`FullStep_FitsRelayRequestLine`) fails if it ever passes 16 KB. A 414 error in
`C:\relay\logs` would mean a step outgrew the cap.

## Security

Credentials travel as unsalted SHA-256 hashes in URL query strings over plain HTTP, and the AES
key that "encrypts" the step is a **constant compiled into every copy of II Library**. It is
obfuscation, not encryption.

- Keep it on the lab LAN. The firewall rule admits only the sim floor's subnet.
- Simulated vitals only. Nothing real or identifying goes in a scenario.
- Accessions are one flat namespace. The edit password is what stops anyone else overwriting a
  room, so stations in `stations.json` always have one.
- The relay never logs request lines, which carry hashes and patient data. It logs only new
  rooms, bad edit passwords and expiry.

If it ever needs to leave the LAN, put it behind HTTPS and a VPN rather than hardening the
protocol.

## FERPA

The relay holds no names or student identifiers. But a room's *current step* is an indirect
record of what its learners did ("Shock" means the team gave the wrong drug), and combined with a
room schedule that is "linked or linkable" to identifiable students. During a graded event,
treat it as assessment data.

- **Host it internally.** This relay keeps the data on a university machine. The public upstream
  relay is a disclosure to a third party; use it only for testing with no students involved.
- **Nothing student-identifying** in room names, accessions, scenario names, step names or
  branch labels. Those strings end up in layouts, launchers and the relay.
- **No end-of-session state is kept.** II Command overwrites a room with a resting default
  patient when its broadcast stops. The relay drops any room not posted to for 12 hours, and a
  restart clears everything.
- `stations.json` and layout (`.iicmd`) files hold room passwords in plain text; treat them like
  any credential file. II Command's session logs (`Documents\Infirmary Integrated\Session Logs`)
  name rooms, not students, but paired with a roster they are education records.
