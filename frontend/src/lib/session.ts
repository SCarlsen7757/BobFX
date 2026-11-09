import { computed, ref } from 'vue';
import { QueryClient, useQuery } from '@tanstack/vue-query';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { api } from './api';
import { newer } from './revisions';
import type { Defaults, DiscoveryState, EffectState, GameState, InstallationState, SoundEvent, UiState, Versioned } from './contracts';

export const queryClient = new QueryClient({ defaultOptions: {
  queries: { staleTime: Infinity, retry: 1, refetchOnWindowFocus: false },
  mutations: { retry: false },
} });
export const connectionStatus = ref<'connecting' | 'syncing' | 'connected' | 'disconnected'>('connecting');
export const connected = computed(() => connectionStatus.value === 'connected');
export const defaults = ref<Defaults>();
export const preview = ref<string[]>([]);
export const suppressedRun = ref<string | null>(null);
let instance = '';
let syncing = true;
let stopped = false;
let previewWanted = false;
let startTask: Promise<void> | undefined;
let syncTask: Promise<void> | undefined;
let retryTimer: ReturnType<typeof setTimeout> | undefined;
let buffered: Array<() => void> = [];
let onSound: ((event: SoundEvent) => void) | undefined;
let connectionGeneration = 0;
const connection = new HubConnectionBuilder().withUrl('/hubs/ui')
  .withAutomaticReconnect({ nextRetryDelayInMilliseconds: context => Math.min(1000 * 2 ** Math.min(context.previousRetryCount, 5), 15000) })
  .configureLogging(LogLevel.None).build();

function apply<T>(key: string, value: Versioned<T>) {
  if (newer(queryClient.getQueryData<Versioned<T>>([key]), value, instance))
    queryClient.setQueryData([key], value);
}
function receive<T>(key: string, value: Versioned<T>) {
  if (syncing) {
    if (buffered.length < 256) buffered.push(() => apply(key, value));
  } else apply(key, value);
}
connection.on('GameStateChanged', (value: Versioned<GameState>) => receive('game', value));
connection.on('EffectStateChanged', (value: Versioned<EffectState>) => receive('effect', value));
connection.on('DevicesChanged', (value: Versioned<DiscoveryState>) => receive('discovery', value));
connection.on('InstallationChanged', (value: Versioned<InstallationState>) => receive('installation', value));
connection.on('LedPreviewChanged', (value: string[]) => { if (connected.value && previewWanted) preview.value = value; });
connection.on('SoundRequested', (event: SoundEvent) => {
  const game = queryClient.getQueryData<Versioned<GameState>>(['game'])?.value;
  if (connected.value && !document.hidden && game?.runId === event.runId && game.phase === 'Ending') onSound?.(event);
});

export function synchronize(): Promise<void> {
  if (syncTask) return syncTask;
  const generation = connectionGeneration;
  syncTask = (async () => {
    syncing = true; connectionStatus.value = 'syncing'; buffered = [];
    const snapshot = await api<UiState>('/api/ui/state');
    if (generation !== connectionGeneration || connection.state !== HubConnectionState.Connected || stopped || document.hidden) return;
    instance = snapshot.game.serverInstanceId;
    apply('game', snapshot.game); apply('effect', snapshot.effect); apply('discovery', snapshot.discovery);
    apply('installation', snapshot.installation);
    defaults.value = snapshot.defaults;
    for (const update of buffered) update();
    buffered = []; syncing = false;
    await synchronizePreview();
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
connection.onreconnecting(() => { connectionGeneration++; syncing = true; connectionStatus.value = 'disconnected'; preview.value = []; });
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
    connectionGeneration++; syncing = true; connectionStatus.value = 'disconnected'; preview.value = []; void connection.stop();
  };
  const online = () => { void start(); };
  window.addEventListener('offline', offline);
  window.addEventListener('online', online);
  return () => {
    stopped = true; onSound = undefined;
    if (retryTimer) clearTimeout(retryTimer);
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
export function useGame() { return useQuery({ queryKey: ['game'], queryFn: async () => (await api<UiState>('/api/ui/state')).game, enabled: false }); }
export function useEffect() { return useQuery({ queryKey: ['effect'], queryFn: async () => (await api<UiState>('/api/ui/state')).effect, enabled: false }); }
export function useDiscovery() { return useQuery({ queryKey: ['discovery'], queryFn: async () => (await api<UiState>('/api/ui/state')).discovery, enabled: false }); }
export function useInstallation() { return useQuery({ queryKey: ['installation'], queryFn: async () => (await api<UiState>('/api/ui/state')).installation, enabled: false }); }
export async function command(path: string, body?: unknown, method = 'POST') {
  if (!connected.value) throw new Error('Reconnect before sending a command.');
  try { await api(path, method, body); await synchronize(); }
  catch (error) {
    // The server may have accepted a command even if its response was lost.
    // Refresh state, but never retry a command automatically.
    void synchronize(); throw error;
  }
}
