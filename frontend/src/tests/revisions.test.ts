import { describe, expect, it } from 'vitest';
import { newer } from '../lib/revisions';
const snapshot = (serverInstanceId: string, revision: number) => ({ serverInstanceId, revision, serverUtc: '', value: {} });
describe('realtime ordering', () => {
  it('rejects older HTTP snapshots and duplicate events', () => {
    expect(newer(snapshot('a', 5), snapshot('a', 4), 'a')).toBe(false);
    expect(newer(snapshot('a', 5), snapshot('a', 5), 'a')).toBe(false);
    expect(newer(snapshot('a', 5), snapshot('a', 6), 'a')).toBe(true);
  });
  it('accepts a restarted server and rejects delayed events from its predecessor', () => {
    expect(newer(snapshot('a', 99), snapshot('b', 1), 'b')).toBe(true);
    expect(newer(snapshot('b', 1), snapshot('a', 100), 'b')).toBe(false);
  });
});
