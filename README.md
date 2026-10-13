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

## Browser regression tests

Playwright tests in [e2e](e2e) drive the real Blazor Server UI using TypeScript and Chromium. They exercise every registered LED effect, Strobe's two color controls, invalid input, game ownership, keyboard pre-countdown Stop, navigation, and full reload. Tests fail on browser console errors, JavaScript exceptions, or a visible **Connection interrupted** banner. Focused .NET tests also simulate disconnected JavaScript interop during audio cleanup.

From the repository root, restore .NET packages, then install and run the browser tests:

```sh
dotnet restore BobFx.slnx --locked-mode
cd e2e
npm ci
npx playwright install chromium
npm test
```

`npm test` publishes and starts a dedicated Production app on `http://127.0.0.1:5187`, waits for readiness, runs the tests sequentially against the shared game state, then stops the app. Keep that port free. UDP output targets loopback, so these tests do not send lighting commands to LAN devices. Discovery still runs, but no test requires a physical device. These tests use actual Blazor/SignalR event handling rather than mocked HTTP responses.

Use `npm run test:ui` for the interactive test runner, `npm run test:headed` to watch Chromium, and `npm run report` to inspect the HTML report. Failed tests retain screenshots, video, browser console output, and Playwright traces under `e2e/test-results`. CI installs Chromium with its Linux dependencies and uploads reports/traces; browser failures block the release quality gate. Physical WLED output and network discovery still need hardware testing.

## Development in Docker

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
- **One output goes black in realtime mode:** configure BobFX's LED count for the device's entire address space, including all physical outputs. For example, two 30-LED outputs starting at indices 0 and 30 require `WLed__Led__LedCount=60`. A 24/30-pixel frame only reaches the first output; WLED clears the remaining pixels when realtime starts. For diagnosis, use the device's IP as the UDP target and start a Solid effect with a non-black color. Check WLED's Peek preview to see which pixel indices receive colors before investigating wiring.
- **Unhealthy:** inspect logs and `docker inspect` health output. Confirm the bind IP exists on the host, is permitted by the CIDR, and has an allowed Host header.
- **No sound:** select **Enable audio** in that browser and check mute/autoplay restrictions. Reenable after a visible playback error to retry.
- **Restore changes unexpectedly:** use `--locked-mode`; deliberate dependency updates must regenerate and commit locks.

## Quality, releases, and issue tracking

`BobFx.slnx` is the sole application/test solution. Docker Compose can be used independently; the Visual Studio Docker project is kept outside the cross-platform solution.

The effect start API preserves the selected effect's descriptor default when `speedMs` is omitted or null. Explicit values must be 10–60000 milliseconds.

See the [modernization review](docs/modernization-review.md) for findings, validation evidence, and remaining rollout checks.

Pull requests and main pushes run locked restore, Release build with analyzers, .NET and JavaScript tests, Playwright browser tests, NuGet advisory checks, repository secret/configuration scans, and hardened image smoke tests for amd64 and arm64. CodeQL runs on pull requests/main and weekly. Dependabot checks NuGet, browser test npm dependencies, Docker images, and pinned GitHub Actions weekly.

Publishing is explicit: a trusted maintainer pushes a stable `vMAJOR.MINOR.PATCH` tag. The release waits for quality and image checks, publishes a versioned multi-architecture GHCR image with SBOM/provenance, and records the immutable production digest in release notes. Main pushes no longer automatically publish `latest` or create releases.

Versions come directly from the stable tag: `v0.7.0` produces `ghcr.io/scarlsen7757/bobfx:0.7.0`, assembly version `0.7.0.0`, and OCI version `0.7.0`. The next planned release is `v0.7.0`; push it only after the application and release follow-ups merge and checks pass.

The release builds a unique candidate once, resolves its exact amd64/arm64 digests, scans each complete image, and smoke-tests the hardened runtime and extracted assembly version. It verifies OCI version/source revision and both platforms' SBOM/provenance before promoting the complete tested index to the stable tag. Promotion does not rebuild. The final stable digest must equal the verified candidate digest; release assets include reports, policy summaries, attestations, assembly versions, and health logs.

Rerunning a release with an existing stable image re-verifies that digest instead of rebuilding it. Its version and source revision must match the tagged commit. A different existing stable digest cannot be overwritten. Failed candidate tags may remain for diagnosis, but do not receive a stable tag or GitHub release. Production deployments continue to use the release's digest, with rollback performed by restoring the previous digest.

Image scans block all HIGH/CRITICAL vulnerabilities, including those without a fix. `.github/vulnerability-exceptions.json` starts empty. An exception requires maintainer review and the fields `id`, `package`, `target` (exact Trivy report target), `rationale`, `owner`, `issue` (a BobFX issue URL), and `expires` (YYYY-MM-DD). Exceptions expire at the start of that UTC date; expired or incomplete entries fail CI. Matching is specific to vulnerability, package, and target, and full reports retain suppressed findings. Prefer fixing/updating dependencies; do not add broad exclusions or enable unfixed filtering.

GitHub automatically generates release notes from **merged PR labels**, using `.github/release.yml`. Categories follow this precedence: Breaking changes, Security, Features, Bug fixes, Performance, Dependencies, Documentation, Maintenance, and Other changes. Each PR appears once; area labels remain useful for classification, and unlabeled PRs remain visible. Dependabot updates are included. Apply work-type labels to PRs themselves: labels on linked issues alone do not categorize release notes. Generated changes, contributors, and the changelog accompany the verified image digest and deployment instructions.

Issues and PRs use plain labels from [.github/labels.json](.github/labels.json): areas such as `api`, `web`, `infra`, `wled`, `audio`; work types such as `bug`, `security`, `performance`, `refactor`, `chore`, `documentation`; and `breaking change` for migrations. There are no priority labels. Link implementation PRs using `Fixes #<issue>` or `Refs #<issue>`.

The modernization findings are tracked in [issue #6](https://github.com/SCarlsen7757/BobFX/issues/6).

Do not commit tokens, deployment `.env` files, certificates, or local build outputs.

## License

See [LICENSE](LICENSE).
