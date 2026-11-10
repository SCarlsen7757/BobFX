import { describe, expect, it, vi } from 'vitest';
vi.mock('../lib/session', () => ({ connected: { value: true }, useEffect: vi.fn(), useGame: vi.fn(), useInstallation: vi.fn() }));
import { boardPixels, swapStrips, twoStripEdges, validateBoards } from '../lib/installation';
import type { Board, InstallationSettings } from '../lib/contracts';

const board = (): Board => ({ id: 'a', name: 'Board 1', deviceMac: '001122334455', rotation: 0, edges: twoStripEdges(16) });
const settings = (boards: Board[]): InstallationSettings => ({ version: 1, boards,
  devices: [{ mac: '001122334455', name: 'WLED', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
  controllers: [], idleSync: { enabled: false, masterMac: null, followers: [], group: 1 },
  animation: { framesPerSecond: 30, flashSeconds: 2, dotSeconds: 3, dotLapsPerSecond: 4 } });

describe('two strips starting at the same corner', () => {
  it('maps both outputs in opposite directions and covers each LED exactly once', () => {
    const pixels = boardPixels(board());
    expect(pixels.map(p => p.index)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 15, 14, 13, 12, 11, 10, 9, 8]);
    expect(new Set(pixels.map(p => p.index)).size).toBe(16);
    expect(() => twoStripEdges(17)).toThrow('divisible by four');
    expect(() => twoStripEdges(2)).toThrow();
  });
  it('swaps output routes without losing indices and supports every quarter turn', () => {
    const mapped = board();
    const original = structuredClone(mapped);
    swapStrips(mapped);
    expect(boardPixels(mapped).map(p => p.index)).toEqual([8, 9, 10, 11, 12, 13, 14, 15, 7, 6, 5, 4, 3, 2, 1, 0]);
    swapStrips(mapped); expect(mapped).toEqual(original);
    for (const [rotation, first] of [[0, 0], [90, 11], [180, 15], [270, 4]]) {
      mapped.rotation = rotation!;
      expect(boardPixels(mapped)[0]!.index).toBe(first);
      expect(new Set(boardPixels(mapped).map(p => p.index)).size).toBe(16);
    }
  });
  it('allows multiple unassigned slots but rejects duplicate controllers and overlapping edges', () => {
    const slot: Board = { id: 'slot1', name: 'Waiting', deviceMac: null, rotation: 0, edges: [] };
    expect(() => validateBoards(settings([slot, board(), { ...slot, id: 'slot2' }]))).not.toThrow();
    expect(() => validateBoards(settings([board(), { ...board(), id: 'b' }]))).toThrow('unique');
    expect(() => validateBoards(settings([{ ...slot, deviceMac: '001122334455' }]))).toThrow('four mapped edges');
    const overlapping = board(); overlapping.edges[1]!.start = 3;
    expect(() => validateBoards(settings([overlapping]))).toThrow('overlap');
  });
});
