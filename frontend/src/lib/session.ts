import { computed, ref, shallowRef } from 'vue';
import { QueryClient, useQuery } from '@tanstack/vue-query';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { api } from './api';
import { decodeBase64 } from './bytes';
import { endpoints } from './endpoints';
import { newer } from './revisions';
import type { BoardFrame, Defaults, DiscoveryState, EffectState, GameState, InstallationState, SoundEvent, UiState, Versioned } from './contracts';

export const queryClient = new QueryClient({ defaultOptions: {
  queries: { retry: 1, refetchOnWindowFocus: false },
  mutations: { retry: false },
} });
// These four are filled by the hub and never fetched, so they never go stale. Anything read over HTTP, such as a
// device's details, is fetched again each time it is shown.
const hubStates = { game: 'GameStateChanged', effect: 'EffectStateChanged', discovery: 'DevicesChanged', installation: 'InstallationChanged' } as const;
type HubState = keyof typeof hubStates;
for (const key of Object.keys(hubStates)) queryClient.setQueryDefaults([key], { staleTime: Infinity });
export const connectionStatus = ref<'connecting' | 'syncing' | 'connected' | 'disconnected'>('connecting');
export const connected = computed(() => connectionStatus.value === 'connected');
export const defaults = ref<Defaults>();
export const preview = ref<string[]>([]);
export const suppressedRun = ref<string | null>(null);
/** What every controller's LEDs show right now, by MAC: RGB bytes, or null while it receives no frames. Empty until a view watches. */
export const boardFrames = shallowRef<Record<string, Uint8Array | null>>({});
let boardWatchers = 0;
let instance = '';
let syncing = true;
let stopped = false;
let previewWanted = false;
let startTask: Promise<void> | undefined;
let syncTask: Promise<void> | undefined;
let syncAgain = false;
let retryTimer: ReturnType<typeof setTimeout> | undefined;
let buffered: Array<() => void> = [];
let onSound: ((event: SoundEvent) => void) | undefined;
let connectionGeneration = 0;
const connection = new HubConnectionBuilder().withUrl(endpoints.hub)
  .withAutomaticReconnect({ nextRetryDelayInMilliseconds: context => Math.min(1000 * 2 ** Math.min(context.previousRetryCount, 5), 15000) })
  .configureLogging(LogLevel.None).build();

function apply<T>(key: HubState, value: Versioned<T>) {
  if (newer(queryClient.getQueryData<Versioned<T>>([key]), value, instance))
    queryClient.setQueryData([key], value);
}
function receive<T>(key: HubState, value: Versioned<T>) {
  if (syncing) {
    if (buffered.length < 256) buffered.push(() => apply(key, value));
  } else apply(key, value);
}
for (const [key, event] of Object.entries(hubStates) as Array<[HubState, string]>)
  connection.on(event, (value: Versioned<unknown>) => receive(key, value));
connection.on('LedPreviewChanged', (value: string[]) => { if (connected.value && previewWanted) preview.value = value; });
function showBoardFrames(frames: BoardFrame[]) {
  boardFrames.value = Object.fromEntries(frames.map(frame => [frame.mac, frame.pixels === null ? null : decodeBase64(frame.pixels)]));
}
connection.on('BoardFramesChanged', (frames: BoardFrame[]) => { if (connected.value && boardWatchers > 0) showBoardFrames(frames); });
connection.on('SoundRequested', (event: SoundEvent) => {
  const game = queryClient.getQueryData<Versioned<GameState>>(['game'])?.value;
  if (connected.value && !document.hidden && game?.runId === event.runId && game.phase === 'Ending') onSound?.(event);
});

export function synchronize(): Promise<void> {
  // The snapshot being fetched may predate what this caller just did, so it is fetched once more before it is used.
  if (syncTask) { syncAgain = true; return syncTask; }
  const generation = connectionGeneration;
  syncTask = (async () => {
    syncing = true; connectionStatus.value = 'syncing'; buffered = [];
    let snapshot: UiState;
    do { syncAgain = false; snapshot = await api<UiState>(endpoints.state); } while (syncAgain);
    if (generation !== connectionGeneration || connection.state !== HubConnectionState.Connected || stopped || document.hidden) return;
    instance = snapshot.game.serverInstanceId;
    apply('game', snapshot.game); apply('effect', snapshot.effect); apply('discovery', snapshot.discovery);
    apply('installation', snapshot.installation);
    defaults.value = snapshot.defaults;
    for (const update of buffered) update();
    buffered = []; syncing = false;
    await synchronizePreview();
    await synchronizeBoards();
    connectionStatus.value = 'connected';
  })().catch(() => {
    connectionStatus.value = 'disconnected';
    scheduleRetry();
  }).finally(() => {
    syncTask = undefined;
    if (!stopped && !document.hidden && connection.state === HubConnectionState.Connected && !connected.value) scheduleRetry();
  });
  return syncTask;
}

function scheduleRetry() {
  if (!stopped && !retryTimer) retryTimer = setTimeout(() => {
    retryTimer = undefined; void start();
  }, 2000);
}
async function start() {
  if (stopped || startTask || document.hidden || !navigator.onLine) return;
  startTask = (async () => {
    if (connection.state === HubConnectionState.Disconnected) {
      connectionStatus.value = 'connecting'; await connection.start();
    }
    if (connection.state === HubConnectionState.Connected) await synchronize();
  })().catch(() => { connectionStatus.value = 'disconnected'; scheduleRetry(); })
    .finally(() => { startTask = undefined; });
  return startTask;
}
connection.onreconnecting(() => { connectionGeneration++; syncing = true; connectionStatus.value = 'disconnected'; preview.value = []; boardFrames.value = {}; });
connection.onreconnected(() => synchronize());
connection.onclose(() => { connectionGeneration++; connectionStatus.value = 'disconnected'; scheduleRetry(); });

export function startSession(sound: (event: SoundEvent) => void) {
  stopped = false; onSound = sound; void start();
  const resume = () => {
    if (!document.hidden) void start();
    else { connectionGeneration++; connectionStatus.value = 'syncing'; syncing = true; }
  };
  document.addEventListener('visibilitychange', resume);
  const offline = () => {
    connectionGeneration++; syncing = true; connectionStatus.value = 'disconnected'; preview.value = []; boardFrames.value = {}; void connection.stop();
  };
  const online = () => { void start(); };
  window.addEventListener('offline', offline);
  window.addEventListener('online', online);
  return () => {
    stopped = true; onSound = undefined;
    // Cleared as well as cancelled: a session started again must be able to schedule its own retry.
    clearTimeout(retryTimer); retryTimer = undefined;
    window.removeEventListener('offline', offline);
    window.removeEventListener('online', online);
    document.removeEventListener('visibilitychange', resume); void connection.stop();
  };
}
export async function setPreview(wanted: boolean) {
  previewWanted = wanted;
  if (!wanted) preview.value = [];
  if (connected.value) {
    try { await synchronizePreview(); }
    catch { connectionStatus.value = 'disconnected'; scheduleRetry(); }
  }
}
async function synchronizePreview() {
  // Navigation may change the desired subscription while an invocation is pending.
  // Reconcile again after its acknowledgement, including during reconnect/resync.
  let requested: boolean;
  do {
    requested = previewWanted;
    await connection.invoke(requested ? 'SubscribePreview' : 'UnsubscribePreview');
  } while (requested !== previewWanted);
}
/**
 * Starts or stops the live board frames for one view. Views are counted, so the frames keep coming while any view
 * still shows them. The call returns once the server has acknowledged the change.
 */
export async function watchBoards(watching: boolean) {
  boardWatchers = Math.max(0, boardWatchers + (watching ? 1 : -1));
  if (boardWatchers === 0) boardFrames.value = {};
  if (connected.value) {
    try { await synchronizeBoards(); }
    catch { connectionStatus.value = 'disconnected'; scheduleRetry(); }
  }
}
async function synchronizeBoards() {
  // As with the preview, the wanted state may change while an invocation is pending.
  let requested: boolean;
  do {
    requested = boardWatchers > 0;
    if (requested) showBoardFrames(await connection.invoke<BoardFrame[]>('SubscribeBoards'));
    else await connection.invoke('UnsubscribeBoards');
  } while (requested !== boardWatchers > 0);
}
/** State the hub keeps up to date. It is never fetched by the query itself, which only exposes what `apply` stored. */
function useHubState<T>(key: HubState) { return useQuery<Versioned<T>>({ queryKey: [key], enabled: false }); }
export function useGame() { return useHubState<GameState>('game'); }
export function useEffect() { return useHubState<EffectState>('effect'); }
export function useDiscovery() { return useHubState<DiscoveryState>('discovery'); }
export function useInstallation() { return useHubState<InstallationState>('installation'); }
export async function command(path: string, body?: unknown, method = 'POST') {
  if (!connected.value) throw new Error('Reconnect before sending a command.');
  try { await api(path, method, body); await synchronize(); }
  catch (error) {
    // The server may have accepted a command even if its response was lost.
    // Refresh state, but never retry a command automatically.
    void synchronize(); throw error;
  }
}
