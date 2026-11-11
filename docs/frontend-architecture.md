# Frontend architecture

BobFX uses Vue, TypeScript, Vue Router, TanStack Vue Query, and an ASP.NET Core SignalR hub. The .NET coordinator remains the only owner of game timing and lighting output. Production serves static frontend assets from the same .NET container; Node is build-only.

## Development and builds

Install Node 26 and the .NET SDK in global.json. `dotnet build` installs the locked frontend packages when needed and builds Vue into `BobFx.Core/wwwroot/ui` (ignored by Git). `dotnet publish` includes those assets. Docker uses a pinned Node build stage and copies the result into the .NET publish stage.

For hot reload, start the backend on http://127.0.0.1:5159 and run `npm --prefix frontend run dev`. Open the Vite URL, normally http://127.0.0.1:5173. Vite proxies `/api`, `/hubs`, and sound files while preserving Host/Origin. Production remains same-origin and retains the existing trusted-network restrictions.

Run `npm --prefix frontend test` for audio and revision-ordering tests. Playwright tests in `e2e` exercise the published application in Chromium and iPad WebKit. A physical iPad is still required for final Safari/audio/fullscreen and sustained GPU checks.

## State and commands

- `GET /api/ui/state` returns versioned game, effect, discovery, and installation snapshots plus configured countdown defaults.
- `/hubs/ui` publishes `GameStateChanged`, `EffectStateChanged`, `DevicesChanged`, `InstallationChanged`, and live `SoundRequested` events. One publisher samples services every 100ms, independently of connected browser count.
- Each state stream has a server-instance ID and increasing revision. Vue Query is the single server-state cache. Events are buffered during initial synchronization, older revisions are rejected, and a new server instance replaces the previous cache.
- Initial failures retry. Reconnect, browser online, and tablet resume fetch current state before enabling commands. Commands are HTTP mutations, never optimistic or automatically retried. Disconnected controls do not queue actions.
- Effects pages join `led-preview` through `SubscribePreview` and leave through `UnsubscribePreview`; disconnected clients are removed. `LedPreviewChanged` frames are sent at most 10 times per second, only when subscribers exist.
- Sound events contain an event ID, run ID, and the supported `end` key. Audio is unlocked directly from a tap on the header speaker icon (or the Game heading icon in fullscreen), muted by default, preserved across routes, and reset on reload. Sound events are deduplicated and are not replayed after reconnect.

The public countdown/effect routes remain compatible. `/api/drgb/start` additionally accepts optional `colors` (#RRGGBB), `fadeDurationSeconds`, `breathDurationSeconds`, `minBrightnessPercent`, and `maxBrightnessPercent`. Omitted fields use existing effect defaults. Validation is atomic; failed commands leave the current output intact.

Device APIs:

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/wled/devices` | Device summaries |
| GET | `/api/wled/devices/{mac}` | Details for a normalized MAC |
| POST | `/api/wled/scan` | Request a server-owned scan; returns 202 and coalesces requests |
| DELETE | `/api/wled/devices` | Clear discovered devices |
| DELETE | `/api/wled/devices/{mac}` | Remove an offline device; online devices return 409 |

Countdown settings remain per-page drafts. Installation settings persist in the data volume, with protected BUSY Bar credentials stored separately. Board layout, light show and controller saves update only their own sections so drafts cannot overwrite changes from another page. Authentication and offline game control are unchanged. The Game page keeps its hidden timer, opening BOB message, game-over behavior, and Stop confirmation. `/game/fullscreen` hides navigation while preserving the connection and audio player.

## Synthwave background

A lazy Three.js chunk draws a centered yellow-to-pink striped sun, a scrolling perspective grid and forward-moving wireframe mountains framing an open central valley. Floor and mountains share one continuous low-poly lattice and vertex shader, independently of Vue updates. The mesh extends behind the camera, while distant and lateral edges fade out to keep moving boundaries offscreen. Rendering is capped at 30fps, with device pixel ratio capped at 1.5. The loop pauses in hidden tabs. Five seconds below 24fps reduces resolution; another slow interval switches to the static scene for the session.

The background animates automatically, with no app toggle or saved opt-out. System reduced-motion settings select the static CSS scene with an SVG mountain fallback. WebGL2 initialization failure or context loss also selects that fallback. Rendering resources, observers, and listeners are released on teardown. The background never encodes game timing.

Application routes are explicitly served by .NET. Missing API, hub, health, and static assets return normal HTTP errors instead of SPA HTML. Rollback uses the previous application image digest.

## Device branch integration

The installation backend from `codex/dev-next-version` (9019a12) is retained with Vue pages at `/layout` and `/controllers`, plus registration/selection/Locate actions on `/wled`. `InstallationOutputService` replaced the legacy broadcast worker, which has been removed along with its `WLed:Udp` settings: only selected registered WLED devices receive output. WLED discovery refreshes saved metadata and probes saved endpoints even when mDNS fails.

`PUT /api/installation/layout` saves boards and idle sync while preserving the other sections. `PUT /api/installation/light-show` saves the light show look and the animation timing the same way. `PUT /api/installation/controllers` saves controller settings plus write-only password replacements while preserving all other sections. `POST /api/installation/controllers/test` authenticates an unsaved draft without enabling integration. Device registration, Locate, selection and removal use `/api/installation/devices`. Existing full-settings and password endpoints remain available.

Installation snapshots publish device errors, controller status, Locate ownership and password-presence flags, never credentials. Configuration writes remain blocked during games, manual lighting output, Locate operations, calibration and light show tests. BUSY Bar physical START / wheel-press and browser controls use the same coordinator and SignalR reflects both immediately.

Board layout uses a single draft and a horizontal row of independently created board slots. Nullable controller assignments allow incomplete layouts to persist. New assignments generate the shared-corner, opposite-direction two-strip mapping; existing mappings remain intact. Advanced mapping and idle sync controls are collapsed by default. Guided calibration sends the unsaved draft to a server-owned preview session, with a serialized update queue and five-second heartbeat. The output worker renders four edge colors plus a clockwise marker on the active board, blacks out the other assigned boards, and expires unrenewed sessions after fifteen seconds. The shared coordinator blocks game/manual/Locate/configuration commands during calibration. Installation snapshots include calibration status, and the Game page disables Start across connected screens. Navigation stops preview output; reconnecting preserves the draft after expiry.

The Light show page at `/lights` keeps one draft of the `lightShow` and `animation` sections. Its test buttons send that unsaved draft to a server-owned test, which the output worker renders through the same path as a game. A test has a fixed length, so it needs no heartbeat; the page stops a test it started when it saves or is left. Installation snapshots include `lightShowTest`, and the Game, Countdown and LED effects pages disable their controls while one plays. The game lights are rendered for the boards only: during a game the manual effect renderer is off, so the LED effects preview strip is dark and `/api/drgb/status` reports `Off` with `streaming: true`.

Docker creates `/app/data` for UID 1654; Compose mounts `bobfx-data` there. The hardened image smoke test saves settings and recreates the container to prove persistence, then checks Vue assets and the absence of Node in the runtime. See [installation documentation](installation.md) for firmware requirements, wiring and native WLED sync.
