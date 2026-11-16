import { onBeforeUnmount, ref, watch } from 'vue';
import { api, apiBeacon, messageOf } from './api';
import { endpoints } from './endpoints';
import { useInstallationState } from './installation';
import { useOnPageHide } from './lifecycle';
import { connected, synchronize } from './session';
import type { Board, CalibrationSession } from './contracts';

export interface CalibrationRequest { boards: Board[]; activeBoardId: string }

/**
 * A check of the layout draft on the real boards. The server holds the lights for the check only as long as it
 * keeps hearing from the page, so the session is renewed on a heartbeat and released however the page is left.
 *
 * @param request The draft and the board to light. It throws while the draft cannot be shown.
 * @param report Told what the operator should know: a failed update, or a check that ended by itself.
 * @param starting True while the page is starting or stopping the check, when the server's state is in between.
 */
export function useCalibrationSession(request: () => CalibrationRequest, report: (message: string) => void, starting: () => boolean) {
  const { calibration } = useInstallationState();
  const sessionId = ref<string>();
  let heartbeat: ReturnType<typeof setInterval> | undefined;
  let updates: Promise<void> = Promise.resolve();
  let left = false;

  async function send<T = void>(path: string, method: string, body?: unknown) {
    const abort = new AbortController();
    const timeout = setTimeout(() => abort.abort(), 5000);
    try { return await api<T>(path, method, body, abort.signal); }
    finally { clearTimeout(timeout); }
  }
  function clear() { clearInterval(heartbeat); heartbeat = undefined; sessionId.value = undefined; }
  /** Shows the current draft on the boards, after any update that is still on its way. */
  function update() {
    const id = sessionId.value;
    if (!id) return;
    updates = updates.catch(() => {}).then(async () => {
      if (sessionId.value === id && connected.value && !document.hidden)
        await send(endpoints.installation.calibrationSession(id), 'PUT', request());
    });
    void updates.catch(failure => { report(`The boards could not be updated: ${messageOf(failure)}`); void synchronize(); });
  }
  async function start() {
    const session = await send<CalibrationSession>(endpoints.installation.calibration, 'POST', request());
    // The page may have been left while the check was starting; nothing would stop that check or its heartbeat.
    if (left) { apiBeacon(endpoints.installation.calibrationSession(session.sessionId), 'DELETE'); return; }
    sessionId.value = session.sessionId;
    heartbeat = setInterval(update, 5000);
    await synchronize();
  }
  async function stop() {
    const id = sessionId.value;
    if (!id) return;
    clearInterval(heartbeat);
    await updates.catch(() => {});
    await send(endpoints.installation.calibrationSession(id), 'DELETE');
    clear();
    await synchronize();
  }
  /** Ends the check without waiting for the answer, so it also works while the page is going away. */
  function release() {
    const id = sessionId.value;
    clear();
    if (id) apiBeacon(endpoints.installation.calibrationSession(id), 'DELETE');
  }
  watch(calibration, current => {
    if (sessionId.value && !starting() && current?.sessionId !== sessionId.value) { clear(); report('The check ended.'); }
  });
  useOnPageHide(release);
  onBeforeUnmount(() => { left = true; release(); });
  return { sessionId, start, stop, update, clear };
}
