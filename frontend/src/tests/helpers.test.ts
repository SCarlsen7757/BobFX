import { afterEach, describe, expect, it, vi } from 'vitest';
vi.mock('../lib/session', () => ({ connected: { value: true }, useEffect: vi.fn(), useGame: vi.fn(), useInstallation: vi.fn() }));
import { api, messageOf } from '../lib/api';
import { boardDots, bySide } from '../lib/boardDrawing';
import { decodeBase64 } from '../lib/bytes';
import { sameValue } from '../lib/draft';
import { fitsController, isIntBetween, wellFormed } from '../lib/installation';
import { usePageStatus } from '../lib/status';
import type { Board } from '../lib/contracts';

afterEach(() => vi.unstubAllGlobals());

describe('comparing a draft with what is saved', () => {
  it('ignores the order properties were written in, but not the order of a list', () => {
    expect(sameValue({ name: 'A', left: { start: 0, count: 8 } }, { left: { count: 8, start: 0 }, name: 'A' })).toBe(true);
    expect(sameValue([{ id: 'a' }, { id: 'b' }], [{ id: 'b' }, { id: 'a' }])).toBe(false);
  });
  it('sees a changed, missing or extra value at any depth', () => {
    expect(sameValue({ boards: [{ left: { start: 0 } }] }, { boards: [{ left: { start: 1 } }] })).toBe(false);
    expect(sameValue({ a: 1 }, { a: 1, b: 2 })).toBe(false);
    expect(sameValue([1, 2], [1, 2, 3])).toBe(false);
    expect(sameValue({ a: null }, { a: {} })).toBe(false);
    expect(sameValue([], {})).toBe(false);
  });
  it('treats a property that is undefined as absent, as JSON does', () => {
    expect(sameValue({ a: 1, b: undefined }, { a: 1 })).toBe(true);
  });
});

describe('strip numbers', () => {
  const strip = (start: number, count: number) => ({ start, count, reversed: false });
  const board = (left: ReturnType<typeof strip> | null, right: ReturnType<typeof strip> | null): Board => ({ id: 'a', name: 'A', deviceMac: 'mac', left, right });
  it('must be whole, start at 0 or later and stay within what WLED can address', () => {
    expect(wellFormed(strip(0, 1))).toBe(true);
    expect(wellFormed(strip(0, 65535))).toBe(true);
    for (const bad of [strip(-1, 4), strip(0, 0), strip(.5, 4), strip(0, 4.5), strip(0, 65536), strip(Number.NaN, 1)])
      expect(wellFormed(bad)).toBe(false);
  });
  it('fit a controller only when both strips are mapped onto LEDs it has', () => {
    expect(fitsController(board(strip(0, 8), strip(8, 8)), 16)).toBe(true);
    expect(fitsController(board(strip(0, 8), strip(8, 9)), 16)).toBe(false);
    expect(fitsController(board(strip(0, 8), null), 16)).toBe(false);
    expect(fitsController(board(strip(0, 8.5), strip(8, 8)), 16)).toBe(false);
  });
  it('are split by side for painting, in drawing order', () => {
    const [left, right] = bySide(boardDots(board(strip(0, 3), strip(3, 2))));
    expect(left.map(dot => dot.index)).toEqual([0, 1, 2]);
    expect(right.map(dot => dot.index)).toEqual([3, 4]);
  });
  it('check whole numbers against a range', () => {
    expect(isIntBetween(10, 10, 60)).toBe(true);
    expect(isIntBetween(60.5, 10, 60)).toBe(false);
    expect(isIntBetween(61, 10, 60)).toBe(false);
    expect(isIntBetween(Number.NaN, 10, 60)).toBe(false);
  });
});

describe('frames', () => {
  it('are decoded from base64 into bytes', () => {
    expect(Array.from(decodeBase64('AP+AAQ=='))).toEqual([0, 255, 128, 1]);
    expect(decodeBase64('')).toHaveLength(0);
  });
});

describe('a page status line', () => {
  it('shows what an action returned and is busy while the action runs', async () => {
    const status = usePageStatus();
    let finish = () => {};
    const save = status.action(() => new Promise<string>(resolve => { finish = () => resolve('Saved.'); }));
    const running = save();
    expect(status.busy.value).toBe(true);
    finish(); await running;
    expect(status.busy.value).toBe(false);
    expect([status.text.value, status.failed.value]).toEqual(['Saved.', false]);
  });
  it('shows why an action failed, and the next action clears it', async () => {
    const status = usePageStatus();
    await status.action(() => { throw new Error('The lights are in use.'); })();
    expect([status.text.value, status.failed.value]).toEqual(['The lights are in use.', true]);
    await status.action((name: string) => `Hello ${name}.`)('board');
    expect([status.text.value, status.failed.value]).toEqual(['Hello board.', false]);
  });
  it('keeps a message the action set itself when it returns nothing', async () => {
    const status = usePageStatus();
    await status.action(() => { status.message.value = 'Check cancelled.'; })();
    expect(status.text.value).toBe('Check cancelled.');
  });
  it('makes a message of anything thrown', () => {
    expect(messageOf(new Error('No.'))).toBe('No.');
    expect(messageOf('plain')).toBe('plain');
  });
});

describe('api calls', () => {
  const respond = (status: number, body: string, headers: Record<string, string> = {}) =>
    vi.stubGlobal('fetch', vi.fn(async () => new Response(body || null, { status, headers })));
  it('send JSON and return the parsed answer', async () => {
    respond(200, '{"mac":"001122334455"}');
    expect(await api<{ mac: string }>('/api/x', 'POST', { a: 1 })).toEqual({ mac: '001122334455' });
    expect(fetch).toHaveBeenCalledWith('/api/x', expect.objectContaining({ method: 'POST', body: '{"a":1}', headers: { 'Content-Type': 'application/json' } }));
  });
  it('return nothing for an empty answer', async () => {
    respond(204, '');
    expect(await api('/api/x', 'DELETE')).toBeUndefined();
    respond(200, '');
    expect(await api('/api/x')).toBeUndefined();
  });
  it('fail with the server\'s explanation, or the status when there is none', async () => {
    respond(409, '{"title":"Conflict","detail":"The game owns the lights."}');
    await expect(api('/api/x', 'POST')).rejects.toThrow('The game owns the lights.');
    respond(400, '{"title":"Bad request"}');
    await expect(api('/api/x', 'POST')).rejects.toThrow('Bad request');
    respond(502, 'not json');
    await expect(api('/api/x', 'POST')).rejects.toThrow('Request failed (502).');
  });
});
