# Board installation and remote controllers

Next-version tracker: [#34](https://github.com/SCarlsen7757/BobFX/issues/34), with configuration #35, WLED #36, layout #37, scenes #38, BUSY Bar #39 and hardware acceptance #40. These changes must be built/published before deploying them; the existing v0.7.0 image does not contain them.

## Devices and layout

1. Configure each WLED controller's actual LED outputs and enable realtime UDP reception. BobFX uses DNRGB packets, split into at most 489 pixels per packet, to support more than 490 pixels without oversized datagrams.
2. Open **WLED devices**, scan, and select the devices BobFX should control. **Locate** pulses only the chosen controller for five seconds, without selecting it implicitly. It returns to its native effect after the one-second realtime lease expires. Locate/settings are unavailable during games; stop manual output first.
3. If discovery is blocked, enter the device's private IPv4 address and HTTP port. BobFX checks `/json/info` before registering it. Device identity is its normalized MAC; rediscovery refreshes its address/count/port and persists valid metadata.
4. Open **Board layout**, add selected devices and reorder the squares with the arrow buttons. Click a square to edit it. There is no fixed number of boards.
5. Map the four physical strips in clockwise perimeter order: top, right, bottom, left. Each edge has a zero-based first index, count, and reverse flag. Reverse means traversal along that physical edge runs from the last index to the first. This supports both a continuous perimeter and independently connected strips. Ranges must fit the device count and cannot overlap.
6. Rotation turns the physical mapping clockwise in 90-degree steps. Top and bottom in the displayed layout always represent the player-facing edges. The preview shows LED positions/indices, sampling long strips for readability. An equal four-edge split is only an initial suggestion; verify it against actual wiring.
7. Save the layout. Settings survive restart; running games do not. Devices selected without a board mapping mirror the legacy game/manual effect; board mappings enable spatial scenes. Unmapped LEDs on mapped boards are black.

After changing WLED's hardware count, scan again before remapping. If a device shrinks below an existing edge range, BobFX reports the mismatch and suppresses that board's frames rather than sending an invalid mapping. Other devices continue.

## Idle and game scenes

Enable idle sync, choose a master, select followers and use group 1–8. All members must use the same native WLED UDP port; change it in WLED first if necessary. BobFX persists the native sync group/brightness/color/effect/palette settings and configures master sending and follower receiving. Configure the master's idle playlist through WLED. After realtime game/manual output ends, BobFX releases realtime mode and mirrors the master's current brightness/color/effect parameters to followers without copying LED bounds or restarting the master's playlist. Subsequent native changes use WLED's notifier. Disabling the setup removes BobFX's previous native sync roles on reachable devices.

Preparation uses the configured pre-countdown duration (five seconds by default): 60% for the scanner across the row, 20% for player top/bottom edges, then 20% with all edges blue. Green GO appears when the game timer starts. Starting without pre-countdown enters GO immediately. GO lasts 0.75 seconds, followed by waves and white pulses whose timing does not depend on remaining game time.

Ending uses three red flashes over two seconds, then one fast red dot for three seconds by default. Neighboring perimeter loops alternate direction to form a figure-eight movement; interior boards continue along their edge to the next handoff. The path wraps to the first board after the final loop. Change flash/dot duration, dot circuits per second, and frame rate in the layout settings. Natural completion and manual Stop during play use this ending; Stop during preparation returns directly to idle. Sound delivery remains per enabled browser.

WLED native sync does not automatically relay incoming realtime pixels. BobFX renders a single timeline and sends each board its own frame. UDP unicast is best effort; these protocols do not guarantee physically simultaneous display or acknowledge receipt. Firmware/router behavior and visual smoothness require hardware checks.

## BUSY Bar

Enable local Wi-Fi HTTP API access on the BUSY Bar and set its local device password. Open **Remote controllers** in BobFX, add the device's private address and HTTP port, and enter that password in **Device API password**. Use **Test connection** to verify it without enabling display control, then **Save controllers**. No cloud API token, manually created password file, or Docker overlay is needed.

This also works when running BobFX directly with `dotnet run --project BobFx.Core --launch-profile http`. Browse to the normal local HTTP address and configure the controller through the same page. Native execution saves application data beside the executable unless `Settings__DataPath` is set. In Docker, the existing `/app/data` volume handles persistence automatically.

The password field is write-only: saved passwords are never filled back into the page or included in installation-settings exports. Leave it blank to keep the current password; enter a new password to replace it. Enabled integrations reconnect when credentials change. Passwords are protected using ASP.NET Data Protection in the application's `credentials` directory, with its key ring stored alongside them. On Linux these directories are restricted to the service user. Back up and protect the entire data directory: the key ring is required to decrypt saved passwords and is not protected from someone with access to the whole volume. Existing Docker secret deployments remain supported as a fallback; a password saved from the web page takes precedence.

Each controller has independent enable, timer-display and remote-control switches; multiple bars are supported. Local HTTP testing is supported without HTTPS enforcement. As with other BobFX controls, use the trusted LAN: HTTP does not encrypt the password in transit.

BUSY Bar screens show rainbow BOB FX / IDLE between games, a blue preparation scanner with PRECOUNT and preparation seconds, green GO, then COUNT with rainbow borders. Preparation seconds are always visible. The Show remaining game timer switch affects only running game time; when hidden, BOB FX replaces the number.

Firmware **1.2.3 / API 27.5** uses native `bicycle0` animation files. BobFX generates and automatically uploads eight small, content-named `.anim` assets on the first observed APPS mode of each connection session; no manual files are needed. Each BUSY Bar plays these locally at approximately 30 FPS (dot playback adjusts its cadence to complete a seamless lap). BobFX sends phase/stage changes, changed numeric countdowns, and a once-per-second display lease refresh, rather than pixel frames. Repeated playback commands use `await_previous_end` to preserve the current cycle. Looping elements expire after fifteen seconds without a refresh, giving the once-per-second heartbeat headroom for Wi-Fi delays. Screen changes replace the existing scene and numeric text together without clearing the canvas first; this avoids exposing the clock between phases. Transient display failures retry after 250 ms, backing off to two seconds, and reuse uploaded assets. Missing/invalid-asset responses trigger asset repair. The three-flash animation plays once, with a lease of its configured duration plus one second; it is never queued again by a heartbeat. All uploads and commands pause outside observed APPS mode or when the selector stream disconnects. Upload failures are retried, including re-uploading the assets in case the device rebooted. Other API versions pause output until their native format is verified. Native format and playback behavior were checked against the official [1.2.3 format definition](https://github.com/busy-app/busybar-firmware/blob/1.2.3/lib/anim_file/anim_file_format.h) and [playback implementation](https://github.com/busy-app/busybar-firmware/blob/1.2.3/lib/anim_file/components/anim_file_start.c).

Hardware acceptance remains required: confirm the initial upload succeeds on 1.2.3, let IDLE run through several lease refreshes without stuttering, check the scanner / player edges / all edges / GO transitions, confirm PRECOUNT stays visible with the game timer hidden, then check the three red flashes and one fast red dot. Test APPS exit/re-entry, Wi-Fi loss/recovery and device reboot. Timing follows the game coordinator, but phase commands and numeric text updates can still arrive late over Wi-Fi; native playback removes Wi-Fi timing from the individual animation frames.

Colors are the default: the RGB front display follows scanner/preparation, green GO, colorful COUNT / BOB FX, and red ending flash/dot phases. Timer mode displays the remaining game time while running, including minute counts above 59. Neither mode displays time of day. The rear monochrome screen is left to the device.

The adapter probes `/api/version` and enables the reviewed API 27.5 input schema at `/api/status/ws`. BobFX monitors the selector even when remote game buttons are disabled. Display writes and START/BACK game commands are allowed only after APPS mode is observed. The controller page shows the detected selector position. Other positions pause outgoing traffic; existing elements finish or expire as described above, and an already in-flight request may finish. The status connection remains open to detect APPS again.

Firmware reports selector movement events but does not provide a reliable initial-position query. On first connection or reconnection, move the selector away from APPS and back. BobFX waits with output paused while the position is unknown, when the stream is unavailable, or when the selector protocol is unsupported. Re-entering APPS resumes the current game phase without restarting the game.

In APPS mode, a physical START press starts a game when idle and BACK stops it when remote controls are enabled. Releases, OK and wheel events are ignored. Duplicate presses are debounced; commands use the same coordinator as browser controls. Failures retry; one controller's failure does not stop another. Disabling/removing a controller cancels its connections. Test connection only probes the API version and works regardless of selector position.

Installed firmware and buttons cannot be verified without the actual bar. Protocol references: [official local HTTP API](https://docs.busy.app/bar/dev/http-api), [official state/input stream guide](https://busy-app.github.io/busylib-py/guides/device-state/), [official protobuf definitions](https://github.com/busy-app/busylib-py/tree/main/src/busylib/state_stream_proto). The reviewed display reference was API 27.5.0 / firmware 1.2.3. WLED references: [JSON API](https://kno.wled.ge/interfaces/json-api/), [native sync](https://kno.wled.ge/interfaces/udp-notifier/), [pixel protocols](https://kno.wled.ge/interfaces/udp-realtime/).

## Storage, backup and API

Production/development Compose mounts a named `bobfx-data` volume at `/app/data`. The image creates the mount point owned by UID 1654. The rest of the production container stays read-only. For a host bind mount instead, create the host directory and give UID 1654 ownership before launching. Do not delete the volume during routine updates.

`settings.json` uses schema version 1. Saves write and flush a temporary file then atomically replace the previous file; rejected settings leave the saved file unchanged. Corrupt/unsupported files fail startup clearly, so restore a compatible backup rather than silently losing configuration. Snapshot copies prevent callers from changing live settings without a save.

Back up configuration through `GET /api/installation` or copy `/app/data/settings.json` out of the container. Restore through `PUT /api/installation` while output is idle, which validates before saving. Configuration exports exclude passwords. To preserve saved credentials too, back up the entire data directory including `credentials` and its `keys` subdirectory; otherwise re-enter passwords through the web page after restoring. Back up before upgrades; rollback requires a settings backup supported by the older application.

Backend operations, subject to existing host/CIDR/origin restrictions:

| Operation | Endpoint |
| --- | --- |
| Read/save complete settings | `GET` / `PUT /api/installation` |
| Check/register a manual device | `POST /api/installation/devices/manual` with `{ "address": "192.168.1.20", "port": 80 }` |
| Locate a registered device | `POST /api/installation/devices/{mac}/locate` |
| Apply saved native idle sync | `POST /api/installation/idle-sync/apply` |
| Output/controller diagnostics | `GET /api/installation/status`, `GET /api/installation/controllers/status` |
| Save/replace a controller password | `PUT /api/installation/controllers/{id}/password` with `{ "password": "your-device-password" }` |
| Check whether a password is saved | `GET /api/installation/controllers/{id}/password` returns only `hasPassword` |

Settings contain `version`, `devices`, ordered `boards`, `idleSync`, `animation` and `controllers`. GET returns the complete shape for editing/import/export. Each board's `edges` array is top/right/bottom/left before rotation. Device entries include MAC, name, address, HTTP port, reported LED count/UDP port and selected flag. JSON keys use camel case. Save conflicts return 409; invalid settings return 400; unreachable manual devices return 502. Saving via the API and applying native idle configuration are separate operations.

## Deployment acceptance

Use ARM64 Raspberry Pi OS and Docker Engine on the travel router LAN. Keep the Pi wired when possible; disable Wi-Fi client isolation. Build/CI and API tests do not establish actual LED receipt, idle playlist recovery, or Pi frame-rate capacity.

Record in [#40](https://github.com/SCarlsen7757/BobFX/issues/40): Pi/router models and firmware, image digest, volume recreation/backup restoration, changed IP rediscovery, each board's Locate, scanner/player edges/GO, smooth running scenes, one fast ending dot, native idle recovery, BUSY Bar input/display/secret authentication, device disconnect/recovery, CPU/memory and achieved frame cadence. The local Docker engine was unavailable and physical visual acceptance was not performed; these checks remain open. CI's amd64/arm64 image smoke now saves settings and recreates the container with the same volume, checking persistence as UID 1654.
