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
