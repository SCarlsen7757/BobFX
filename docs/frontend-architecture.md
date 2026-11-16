# Frontend architecture

BobFX uses Vue, TypeScript, Vue Router, TanStack Vue Query, and an ASP.NET Core SignalR hub. The .NET coordinator remains the only owner of game timing and lighting output. Production serves static frontend assets from the same .NET container; Node is build-only.

## Development and builds

Install the Node version in `.nvmrc` and the .NET SDK in global.json. `dotnet build` installs the locked frontend packages when needed and builds Vue into `BobFx.Core/wwwroot/ui` (ignored by Git). `dotnet publish` includes those assets. Docker uses a pinned Node build stage and copies the result into the .NET publish stage.

For hot reload, start the backend on http://127.0.0.1:5159 and run `npm --prefix frontend run dev`. Open the Vite URL, normally http://127.0.0.1:5173. Vite proxies `/api`, `/hubs`, and sound files while preserving Host/Origin. Production remains same-origin and retains the existing trusted-network restrictions.

Run `npm --prefix frontend test` for the unit tests: audio, revision ordering, the session's synchronization and retry, the installation draft rules and the shared helpers. `npm --prefix frontend run typecheck` checks the app with `vue-tsc` and `vite.config.ts` with its own Node configuration. The frontend stays on TypeScript 6 while `vue-tsc` needs the JavaScript compiler; the browser tests, which use plain `tsc`, are on TypeScript 7. Playwright tests in `e2e` exercise the published application in Chromium and iPad WebKit. A physical iPad is still required for final Safari/audio/fullscreen and sustained GPU checks.

## State and commands

- `GET /api/ui/state` returns versioned game, effect, discovery, and installation snapshots plus configured countdown defaults and `limits`: the ranges the server accepts for the light show, animation, countdown, effect speed and sync group (`SettingLimits` on the server). Forms validate against those, so the two cannot disagree.
- `/hubs/ui` publishes `GameStateChanged`, `EffectStateChanged`, `DevicesChanged`, `InstallationChanged`, and live `SoundRequested` events. The event names are the methods of `IUiClient` in `Hubs/UiHub.cs`. One publisher samples services every 100ms, independently of connected browser count.
- Each state stream has a server-instance ID and increasing revision. Vue Query is the single server-state cache. Events are buffered during initial synchronization, older revisions are rejected, and a new server instance replaces the previous cache.
- Initial failures retry. Reconnect, browser online, and tablet resume fetch current state before enabling commands. Commands are HTTP mutations, never optimistic or automatically retried. Disconnected controls do not queue actions.
- Effects pages join `led-preview` through `SubscribePreview` and leave through `UnsubscribePreview`; disconnected clients are removed. `LedPreviewChanged` frames are sent at most 10 times per second, only when subscribers exist.
- Views that show the boards live join `live-boards` through `SubscribeBoards`, which returns the current picture, and leave through `UnsubscribeBoards`. `BoardFramesChanged` carries one `{ mac, pixels }` per selected controller at the animation frame rate, only while subscribers exist and the picture changes. `session.ts` counts the views that watch (`watchBoards`), so the frames keep coming while any view still shows them.
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

## Shared code

Pages are thin; what several of them need is in `frontend/src/lib`:

| Module | Holds |
| --- | --- |
| `session.ts` | The hub connection, synchronization, `command`, and the four hub-fed states (`useGame`, `useEffect`, `useDiscovery`, `useInstallation`) |
| `endpoints.ts`, `routes.ts` | Every API address, and the page list the router and the navigation are both built from |
| `installation.ts` | `useInstallationState` (the parts of the installation snapshot pages read), `useLightsOwner` and `useCanConfigure` (who has the lights), and the board, strip and idle sync rules |
| `draft.ts` | `useSectionDraft`: a page's draft of its section, `dirty`, and the unsaved-changes guard |
| `status.ts` | `usePageStatus`: a page's status line and the actions that write to it; an action returns its message or throws |
| `calibration.ts`, `lightShowTest.ts` | The check session of Board layout, and starting and stopping a light show test |
| `boardDrawing.ts`, `strips.ts`, `bytes.ts` | The drawing of a board, the strip number rules it shares with the forms, and frame decoding |
| `lifecycle.ts`, `unsaved.ts` | `useOnPageHide`, `useNow`, `useUnsavedGuard` |

A page creates one `usePageStatus` (Devices has one for discovery and one for registered devices), wraps each thing a button does in `status.action`, and shows the result with `components/StatusMessage.vue`. The lights have one owner at a time on the server; `useLightsOwner` is the only place the frontend works out which, so every page disables its controls by the same rule.

## Icons and text

Pages carry no explanatory paragraphs; how to set things up is in the [installation documentation](installation.md). Icons come from `@lucide/vue`, imported one by one so only the used ones are bundled. `components/IconButton.vue` is a button that shows only an icon: its `label` is both the `aria-label` and the tooltip, so every action keeps a name for screen readers and for tests. Buttons that keep a word (Save, Scan, the navigation, the light show segments) set the full name as `aria-label` when the visible word is shorter. The top navigation is icons only, each link named by its `aria-label` and tooltip, and its header is sticky at the top of the window. `lib/segments.ts` holds the three light show segments with their icons for the preview and both test rows. State that a picture already shows, such as a live board being idle, stays in the markup as visually hidden text (`.sr-only`) and is spelled out only when something is wrong.

## Saving

Board layout, Light show, Remote controllers and Countdown settings keep a draft and share `components/SaveBar.vue`: a save row that is sticky at the bottom of the window, highlighted with "Unsaved changes" while the page's `dirty` value is true, and repeating the page's status message next to the button. `useSectionDraft` in `lib/draft.ts` gives the three installation pages their draft: `dirty` compares it with the saved section of the installation snapshot whatever order the properties are in, and after a save each page continues from the saved values (`adopt`), so the comparison cannot stay true over something the server normalized. It calls `useUnsavedGuard(dirty)` from `lib/unsaved.ts`, which asks before the route is left and registers `beforeunload` for closing or reloading the tab. Pages create their draft before their own leave guard, so declining keeps a running check or test going.

## Synthwave background

A lazy Three.js chunk draws a centered yellow-to-pink striped sun, a scrolling perspective grid and forward-moving wireframe mountains framing an open central valley. Floor and mountains share one continuous low-poly lattice and vertex shader, independently of Vue updates. The mesh extends behind the camera, while distant and lateral edges fade out to keep moving boundaries offscreen. Rendering is capped at 30fps, with device pixel ratio capped at 1.5. The loop pauses in hidden tabs. Five seconds below 24fps reduces resolution; another slow interval switches to the static scene for the session.

The background animates automatically on the Game page, with no app toggle or saved opt-out. Settings pages have no panel; their forms sit straight on the background, so there it switches to the static CSS scene under a dark overlay to keep text readable, and returning to the Game page starts the animation again. System reduced-motion settings select the static CSS scene with an SVG mountain fallback. WebGL2 initialization failure or context loss also selects that fallback. Rendering resources, observers, and listeners are released on teardown. The background never encodes game timing.

Application routes are explicitly served by .NET. Missing API, hub, health, and static assets return normal HTTP errors instead of SPA HTML. Rollback uses the previous application image digest.

## Device branch integration

The installation backend has Vue pages at `/layout`, `/boards` and `/controllers`, plus registration/selection/Locate actions on `/wled`. `InstallationOutputService` replaced the legacy broadcast worker, which has been removed along with its `WLed:Udp` settings: only selected registered WLED devices receive output. WLED discovery refreshes saved metadata and probes saved endpoints even when mDNS fails; simulated controllers are never probed.

`PUT /api/installation/layout` saves boards and idle sync while preserving the other sections. `PUT /api/installation/light-show` saves the light show look and the animation timing the same way. `PUT /api/installation/controllers` saves controller settings plus write-only password replacements while preserving all other sections. `POST /api/installation/controllers/test` authenticates an unsaved draft without enabling integration. Device registration, Locate, selection and removal use `/api/installation/devices`. Existing full-settings and password endpoints remain available.

Installation snapshots publish device errors, controller status, Locate ownership and password-presence flags, never credentials. Configuration writes remain blocked during games, manual lighting output, Locate operations, calibration and light show tests. BUSY Bar physical START / wheel-press and browser controls use the same coordinator and SignalR reflects both immediately.

Board layout drafts only what it saves, the boards and idle sync; controllers are read live from the installation snapshot, so a controller registered or rescanned while the page is open is used at once. It shows a horizontal row of independently created board slots. Nullable controller assignments allow incomplete layouts to persist. A board has a left and a right strip; a new assignment maps the first half of the controller's LEDs to the left strip and the second half to the right, both from the near end, and an existing mapping that still fits is kept. Swap, reverse, turn and reset are single actions in `lib/installation.ts`. `boardProblems` and `idleSyncProblems` mirror the server's checks and return problems tied to a board and field, so the page shows them while editing and keeps Save off, instead of throwing on save. The first LED and LED count fields and idle sync are collapsed under Advanced settings. Checking the boards sends the unsaved draft to a server-owned preview session, with a serialized update queue and five-second heartbeat. The output worker lights the active board's left strip blue and right strip amber with a dot running from the near end of each, blacks out the other assigned boards, and expires unrenewed sessions after fifteen seconds. The shared coordinator blocks game/manual/Locate/configuration commands during a check. Installation snapshots include its status, and the Game page disables Start across connected screens. Navigation stops preview output; reconnecting preserves the draft after expiry.

`lib/boardDrawing.ts` holds the one drawing of a board: an upright rectangle with the near end at the bottom and a strip of dots down each side. Board layout draws every LED as an SVG dot. `paintBoards` paints the light show preview and `LiveBoards.vue` on a canvas as a lit border instead: each strip's light is put in an image one pixel wide with a row per LED and stretched along its side of the board with smoothing, so neighboring LEDs blend into a gradient. The near and far ends of a board have no LEDs and are left unlit. Wider, fainter passes under each border stand in for the glow. The light is added to the dark board, so an LED that is off leaves the board's faint outline. `LiveBoards` is on the Live boards page at `/boards`, in the check panel of Board layout and beside the test buttons on Light show. On the server every frame leaves through `InstallationOutputService.SendAsync`, which records it in `BoardFrameMonitor` and sends UDP unless the controller is simulated; `BoardMonitorService` samples the monitor for the hub. A frame older than the one-second realtime lease counts as idle. A simulated controller's frames are drawn by its fixed standard wiring and a real controller's through its board's mapping.

The Light show page at `/lights` keeps one draft of the `lightShow` and `animation` sections. Its preview posts the draft to `/api/installation/light-show/preview`, which renders the segment's frames through `BoardSceneRenderer` and returns them base64 encoded; the page paints them with `paintBoards` at `boardDots` positions and asks again after a setting changes. Its test buttons send that unsaved draft to a server-owned test, which the output worker renders through the same path as a game. A test has a fixed length, so it needs no heartbeat; the page stops a test it started when it saves or is left. Installation snapshots include `lightShowTest`, and the Game and LED effects pages disable their controls while one plays. Countdown settings can always be saved: they take effect from the next game. The game lights are rendered for the boards only: during a game the manual effect renderer is off, so the LED effects preview strip is dark and `/api/drgb/status` reports `Off` with `streaming: true`.

Docker creates `/app/data` for UID 1654; Compose mounts `bobfx-data` there. The hardened image smoke test saves settings and recreates the container to prove persistence, then checks Vue assets and the absence of Node in the runtime. See [installation documentation](installation.md) for firmware requirements, wiring and native WLED sync.
