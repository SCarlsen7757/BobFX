# BobFX

BobFX provides light and sound effects for **Bob**, a board game related to Carrom. A shared game countdown drives RGB effects on multiple WLED devices. The application uses ASP.NET Core / Blazor Server on .NET 10 LTS; browsers control one shared game, and the server broadcasts WLED realtime RGB frames over UDP.

When realtime output stops, WLED's realtime lease expires and the devices resume their configured idle playlist. This release preserves that behavior; a new idle experience is separate work.

## Run locally

Install the .NET SDK selected by [global.json](global.json), then run. Browser audio unit tests additionally use Node.js 22 or later, without npm dependencies:

```sh
dotnet restore BobFx.slnx --locked-mode
dotnet build BobFx.slnx --configuration Release --no-restore
dotnet test BobFx.slnx --configuration Release --no-build
node --test BobFx.Core.Tests/BrowserAudio.test.mjs
dotnet run --project BobFx.Core --launch-profile http
```

Open http://localhost:5159. Game state exists in memory; restarting resets it. No database or frontend npm build is required. Navigation and WLED discovery are available in production. Manual effect controls are rejected while a game is active or ending; stop the game first.

For development in Docker:

```sh
docker compose -f docker-compose.yml up --build
```

This development file publishes HTTP on loopback only. The Visual Studio override is intentionally empty, so default Compose launches stay development-only. Docker Desktop bridge networking may not carry LAN broadcast/mDNS: use native execution or the Linux production setup for real hardware tests.

## Production on a Linux LAN host

Use a Linux Docker Engine host on the **same LAN/subnet as the WLED devices**. Production uses host networking for UDP broadcast and mDNS and binds HTTP to an explicitly selected host address. Enable WLED realtime UDP reception on port 21324, configure LED counts, and configure the playlist each device should resume when realtime ends.

1. Copy `.env.example` to `.env`.
2. Set `BOBFX_IMAGE_DIGEST` to the full `sha256:...` digest from a published GitHub release. Replace the sample host IP, trusted client CIDR, hostnames, and subnet broadcast address.
3. Validate and launch the **standalone** production file:

```sh
docker compose --env-file .env -f compose.prod.yaml config --quiet
docker compose --env-file .env -f compose.prod.yaml pull
docker compose --env-file .env -f compose.prod.yaml up -d
docker compose --env-file .env -f compose.prod.yaml ps
docker compose --env-file .env -f compose.prod.yaml logs --tail 100 bobfx
```

Open `http://<BOBFX_BIND_ADDRESS>:5159` from an allowed operator device. Required variables have no permissive defaults. The trusted CIDR must include the bind address so the container's HTTP health probe is allowed; the example assumes operators and server share one trusted subnet. For finer controls, configure additional indexed `Security__AllowedClientNetworks__N` entries in Compose.

The service runs as UID 1654, drops Linux capabilities, denies privilege escalation, uses a read-only filesystem with bounded temporary storage, limits CPU/memory/processes, and rotates logs. No application state is persisted. Temporary ASP.NET Data Protection keys change on recreation, so reload open browser tabs after an upgrade.

This is a **trusted-LAN deployment**, without per-user login. Anyone in an allowed client network can control the shared game. WLED UDP and device HTTP traffic are unauthenticated and unencrypted. Keep WLED and BobFX off the public internet; firewall HTTP 5159 to operator clients. Network access needs outbound UDP 21324 to devices, multicast mDNS UDP 5353, and outbound HTTP to discovered WLED devices. Wi-Fi client isolation or VLAN boundaries can block discovery and output. Host networking has no Docker port mapping.

For updates, replace the image digest, pull, and recreate. For rollback, restore the previous digest and recreate using the same commands. Keep both digests in deployment records.

## Configuration and migration

Configuration uses normal ASP.NET Core precedence: appsettings, environment-specific appsettings, environment variables, then command-line values. Environment keys use `__` for nested sections. Invalid values fail at startup.

| Environment key | Default / constraint |
| --- | --- |
| `WLed__Led__LedCount` | 30; 1–490 LEDs per realtime frame |
| `WLed__Udp__TargetAddress` | 255.255.255.255; IPv4 broadcast or unicast address |
| `WLed__Udp__TargetPort` | 21324; 1–65535 |
| `WLed__Udp__UpdateIntervalMs` | 16; 10–500 ms |
| `WLed__Discovery__UpdateIntervalSeconds` | 60; 5–3600 seconds |
| `Countdown__PreDurationSeconds` | 5; 0–60 seconds |
| `Countdown__DurationSeconds` | 600; 1–86400 seconds |
| `Countdown__DeviationSeconds` | 120; nonnegative and below duration |
| `AllowedHosts` | Explicit semicolon-separated names/IPs required in Production |
| `Security__AllowedClientNetworks__0` | Explicit trusted CIDR required in Production |
| `ASPNETCORE_URLS` | Production Compose sets the required LAN bind address |
| `Logging__LogLevel__Default` | Information |

The production `.env` file exposes short deployment names such as `WLED_LED_COUNT`, then Compose maps them to the hierarchical application keys above. Outside Compose, use the hierarchical keys directly.

Breaking migration changes:

- Upgrade developer/runtime tooling from .NET 9 to .NET 10 and restore from checked-in package locks.
- Replace legacy flat application configuration (`WLED_*`, `PRE_COUNTDOWN_*`, `COUNTDOWN_*`) with the table's hierarchical keys. The former Compose `*_TIME_SECONDS` keys never matched application `*_DURATION_SECONDS` keys.
- Replace the ignored `CountdownConfiguration` JSON section with `Countdown`.
- Replace implicit production/development Compose merging with explicit `-f compose.prod.yaml`. Production requires an image digest, bind address, allowed hosts, trusted CIDR, and broadcast address.
- Effect start/speed endpoints now require JSON bodies instead of query parameters. Game start endpoints accept an optional JSON body; omitted settings use configured defaults. Invalid input returns Problem Details with HTTP 400; output ownership conflicts return HTTP 409. Browser calls must use the application's own origin; trusted CLI callers can omit Origin. No wildcard/public production configuration is supported.
- Browser audio defaults muted. Each browser must select **Enable audio**; **Mute audio** disables only that browser. Reloading a tab resets this opt-in. Enabled clients hear the existing game-over sound; playback errors appear beside the control.

Example commands from a trusted LAN client, while no game owns the output:

```sh
curl -X POST http://bobfx.local:5159/api/drgb/start \
  -H 'Content-Type: application/json' -d '{"effect":"Rainbow","speedMs":100}'
curl -X POST http://bobfx.local:5159/api/drgb/stop
curl -X POST http://bobfx.local:5159/api/countdown/start-with-pre \
  -H 'Content-Type: application/json' \
  -d '{"preDurationSeconds":5,"durationSeconds":600,"deviationSeconds":120}'
curl -X POST http://bobfx.local:5159/api/countdown/stop
```

Replace `bobfx.local` with a configured allowed hostname or the host LAN IP.

## Health and troubleshooting

`/health/live` reports that the HTTP process is alive. `/health/ready` checks the renderer/coordinator and active-stream UDP failures and backs the Docker healthcheck. `/health/status` exposes renderer, UDP, and discovery diagnostics, including last send/scan timestamps. No discovered WLED devices is a valid startup state. A successful UDP send does not acknowledge device receipt. Docker's restart policy restarts exited processes; an unhealthy status alone does not restart a running container.

- **403 / rejected control:** check the client's actual source address against the trusted CIDRs, hostnames against `AllowedHosts`, and use the application's own origin.
- **No WLED devices:** check UDP 5353 multicast, same-LAN connectivity, Wi-Fi client isolation, and device HTTP reachability.
- **Discovery works but LEDs do not:** verify realtime UDP is enabled, port 21324, broadcast address, LED count, and active game/effect status. Discovery and realtime output are independent.
- **Unhealthy:** inspect logs and `docker inspect` health output. Confirm the bind IP exists on the host, is permitted by the CIDR, and has an allowed Host header.
- **No sound:** select **Enable audio** in that browser and check mute/autoplay restrictions. Reenable after a visible playback error to retry.
- **Restore changes unexpectedly:** use `--locked-mode`; deliberate dependency updates must regenerate and commit locks.

## Quality, releases, and issue tracking

See the [modernization review](docs/modernization-review.md) for findings, validation evidence, and remaining rollout checks.

Pull requests and main pushes run locked restore, Release build with analyzers, tests, NuGet advisory checks, repository secret/configuration scans, and hardened image smoke tests for amd64 and arm64. CodeQL runs on pull requests/main and weekly. Dependabot checks NuGet, Docker images, and pinned GitHub Actions weekly.

Publishing is explicit: a trusted maintainer pushes a stable `vMAJOR.MINOR.PATCH` tag. The release waits for quality and image checks, publishes a versioned multi-architecture GHCR image with SBOM/provenance, and records the immutable production digest in release notes. Main pushes no longer automatically publish `latest` or create releases.

Issues and PRs use plain labels from [.github/labels.json](.github/labels.json): areas such as `api`, `web`, `infra`, `wled`, `audio`; work types such as `bug`, `security`, `performance`, `refactor`, `chore`, `documentation`; and `breaking change` for migrations. There are no priority labels. Link implementation PRs using `Fixes #<issue>` or `Refs #<issue>`.

The modernization findings are tracked in [issue #6](https://github.com/SCarlsen7757/BobFX/issues/6).

Do not commit tokens, deployment `.env` files, certificates, or local build outputs.

## License

See [LICENSE](LICENSE).
