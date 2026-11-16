import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { UiState } from '../lib/contracts';

// The hub connection is created when the session module loads, so each test loads the module afresh over a fake hub.
const hub = vi.hoisted(() => ({
  state: 'Disconnected',
  on: vi.fn(), onreconnecting: vi.fn(), onreconnected: vi.fn(), onclose: vi.fn(),
  start: vi.fn(), stop: vi.fn(), invoke: vi.fn(),
}));
vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
  LogLevel: { None: 0 },
  HubConnectionBuilder: class {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return hub; }
  },
}));
const api = vi.hoisted(() => vi.fn());
vi.mock('../lib/api', () => ({ api }));

const versioned = (revision: number) => ({ serverInstanceId: 'server', revision, serverUtc: '', value: { revision } });
const snapshot = (revision: number) => ({ game: versioned(revision), effect: versioned(revision), discovery: versioned(revision),
  installation: versioned(revision), defaults: { durationSeconds: 720, deviationSeconds: 120 } }) as unknown as UiState;

beforeEach(() => {
  vi.resetModules();
  hub.state = 'Disconnected';
  hub.start.mockReset().mockImplementation(async () => { hub.state = 'Connected'; });
  hub.stop.mockReset().mockImplementation(async () => { hub.state = 'Disconnected'; });
  hub.invoke.mockReset().mockResolvedValue([]);
  api.mockReset();
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });
/** Loads the module first: Vue must not find the stand-in document below when it loads. */
async function load() {
  const session = await import('../lib/session');
  const listeners = { addEventListener: vi.fn(), removeEventListener: vi.fn() };
  vi.stubGlobal('document', { hidden: false, ...listeners });
  vi.stubGlobal('window', listeners);
  vi.stubGlobal('navigator', { onLine: true });
  return session;
}

describe('the session', () => {
  it('fetches the snapshot again when a sync is asked for while one is on its way', async () => {
    const answers: Array<(state: UiState) => void> = [];
    api.mockImplementation(() => new Promise<UiState>(resolve => answers.push(resolve)));
    const session = await load();
    const stop = session.startSession(() => {});
    await vi.waitFor(() => expect(answers).toHaveLength(1));

    // Whoever asks now may have just changed something that the first answer does not include.
    const asked = session.synchronize();
    answers[0]!(snapshot(1));
    await vi.waitFor(() => expect(answers).toHaveLength(2));
    expect(session.connected.value).toBe(false);
    answers[1]!(snapshot(2));
    await asked;

    expect(session.queryClient.getQueryData(['installation'])).toMatchObject({ revision: 2 });
    expect(session.connected.value).toBe(true);
    stop();
  });

  it('retries a failed connection after it was stopped and started again', async () => {
    vi.useFakeTimers();
    hub.start.mockRejectedValue(new Error('The server is down.'));
    const session = await load();
    session.startSession(() => {})();
    await vi.advanceTimersByTimeAsync(0);
    hub.start.mockClear();

    const stop = session.startSession(() => {});
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.start).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(2000);
    expect(hub.start).toHaveBeenCalledTimes(2);
    stop();
  });

  it('applies hub updates in revision order and ignores another server instance', async () => {
    api.mockResolvedValue(snapshot(5));
    const session = await load();
    const stop = session.startSession(() => {});
    await vi.waitFor(() => expect(session.connected.value).toBe(true));
    const installationChanged = hub.on.mock.calls.find(([event]) => event === 'InstallationChanged')![1] as (value: unknown) => void;

    installationChanged(versioned(4));
    expect(session.queryClient.getQueryData(['installation'])).toMatchObject({ revision: 5 });
    installationChanged(versioned(6));
    expect(session.queryClient.getQueryData(['installation'])).toMatchObject({ revision: 6 });
    installationChanged({ ...versioned(9), serverInstanceId: 'another' });
    expect(session.queryClient.getQueryData(['installation'])).toMatchObject({ revision: 6 });
    stop();
  });
});
