# Modernization review

Review baseline: `b369a2e1fcc522cc030bad7e01d40b827e4b0753`.
This implementation updates the backend, Blazor frontend, deployment, and maintenance tooling together. The supported deployment is a Linux host on the WLED LAN with explicitly trusted operator networks. WLED idle playlists remain unchanged.

## Findings and implementation

The [GitHub tracker #6](https://github.com/SCarlsen7757/BobFX/issues/6) links 15 findings (#7–#21) with baseline source links, impact, proposed changes, and acceptance criteria. Priorities are recorded in issue bodies. The [label catalog](../.github/labels.json) follows sail-sight's short, plain style, with WLED and audio labels added for this project.

| Reviewed concern | Implemented change |
| --- | --- |
| Unrestricted controls and browser origins | Production CIDR and host restrictions cover HTTP and Blazor; foreign browser origins are rejected. |
| Detached countdown tasks and obsolete transitions | One coordinator serializes commands and advances monotonic game deadlines. |
| Competing render loops and mutable shared frames | One renderer owns frames; consumers receive copies/coherent snapshots; game/manual ownership is explicit. |
| Invalid configuration and partial command mutations | Validated startup options, typed JSON contracts, and validation before mutation with HTTP 400/409 responses. |
| Old runtime and unnecessary dependency | .NET 10 LTS, SDK selection, package locks, removal of explicit System.Text.Json package. |
| Unsafe or inconsistent discovery | Bounded private-IPv4 discovery, HTTP limits, endpoint refresh, serialized scans, and protection against stale scan results after removal. |
| Blazor subscriptions and polling lifetime | Cancellation/disposal of page work and subscriptions, bounded preview polling, and shared validation. |
| Broken assets and inaccessible controls | Local styling, discoverable production navigation, native dialog focus/keyboard behavior. |
| Browser audio lifecycle | Independent muted-by-default browser sessions, gesture-based activation, allowlisted end sound, and disposal of media/listeners. |
| Frame overhead and diagnostics | Pooled UDP buffers, coherent frame copying, send/discovery/renderer health diagnostics, and bounded failure retries. |
| Development/production deployment coupling | Standalone production Compose with explicit LAN configuration and immutable image digest. |
| Container privileges and supply chain | Nonroot, read-only, bounded resources/logs/temp storage, pinned base images/actions, and least-privilege release workflow. |
| Missing maintenance gates | Locked builds, tests, advisory checks, CodeQL, Trivy, Dependabot, and amd64/arm64 image smoke checks in CI. |
| Missing operating documentation | README with setup, production operations, rollback, configuration, and breaking migration details. |

## Validation evidence

Local validation on 2026-10-12:

- Locked dependency restore using the official downloaded packages and local cache.
- Release build and .NET tests with warnings treated as errors: 47 tests passed, including UDP retry and cancellation regressions.
- Four Node tests passed for audio activation, independent sessions, failed playback retry, and disposal. Local execution used `--test-isolation=none` because this environment restricts child processes.
- Release publish succeeded.
- Development and production Compose configuration validation succeeded.
- Workflow validation with actionlint succeeded; shellcheck and pyflakes were unavailable.
- Git whitespace checks, JavaScript syntax checks, and Python script compilation succeeded.
- The official NuGet advisory index contained no matching package IDs for the resolved dependency graph at review time. Local restore used `NuGetAudit=false` due restricted shell network access; CI performs the normal advisory check.

At that validation, container builds, container vulnerability scans, browser interaction/visual checks, and physical WLED tests had **not** run locally. The Docker daemon was inaccessible and no browser surface was available. CI configuration is not evidence that CI has passed.

Browser regression validation on 2026-10-13:

- A Chromium Playwright test reproduced the LED page's Strobe color-index failure, circuit termination, and the subsequent `BrowserAudio.DisposeAsync` `JSDisconnectedException`. Strobe now populates both descriptor color inputs, and audio initialization/disposal handles a disconnected circuit.
- Five TypeScript E2E tests passed against the published Production app: Strobe controls, all registered effects, invalid input preserving output, manual/game ownership with keyboard pre-countdown cancellation, and navigation/full reload. Tests check real server state, browser errors, and the connection-error banner. Output targets loopback; these are not physical WLED acceptance tests.
- Six .NET audio component regression cases cover normal disposal, disconnection during initialization/session/module cleanup, managed callback release, and propagation of unexpected errors. The four disconnection cases failed before the fix.
- Release analyzer build completed with zero warnings/errors; all 53 .NET tests, TypeScript checking, and four JavaScript audio tests passed. CI now runs browser tests in the release quality gate and preserves failure artifacts. Hosted CI and container/hardware results remain separate acceptance evidence.

Before production rollout, run CI and test on the actual Linux LAN host with multiple WLED devices: broadcast output, discovery, packet/LED limits, rapid Stop/Start, manual effect ownership, device idle playlist recovery, and browser audio across two clients. UDP send success does not prove device receipt. This deployment has no per-user authentication; anyone in the allowed networks can operate it.

## GitHub publication

The maintainer ran the label and issue publication scripts. Public GitHub API reads on 2026-10-13 verified all 20 catalog labels, [tracker #6](https://github.com/SCarlsen7757/BobFX/issues/6), and findings #7–#21. The tracker links every finding.

Push the local branch and open an implementation PR linked to the tracker and relevant findings. The temporary publication scripts and issue drafts have been removed after publication.

The local commits separate tracking/labels, the coupled runtime/application changes, and deployment/documentation. Backend and frontend contracts must migrate together; review and deploy the complete branch. Do not deploy an intermediate commit.
