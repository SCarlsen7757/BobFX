export async function api<T = void>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    method, credentials: 'same-origin', cache: 'no-store', signal,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status}).`);
  }
  return response.status === 204 || response.headers.get('content-length') === '0'
    ? undefined as T
    : response.text().then(text => text ? JSON.parse(text) as T : undefined as T);
}

/**
 * Sends a request that outlives the page, for releasing something on the server while the page goes away.
 * Nothing can be done about a failure by then, so there is no answer to wait for.
 */
export function apiBeacon(path: string, method: string) {
  void fetch(path, { method, credentials: 'same-origin', keepalive: true }).catch(() => {});
}

/** What to show for something that was thrown, which need not be an Error. */
export function messageOf(failure: unknown) {
  return failure instanceof Error ? failure.message : String(failure);
}
