import { describe, expect, it, vi } from 'vitest';
vi.mock('../lib/session', () => ({ connected: { value: true }, useEffect: vi.fn(), useGame: vi.fn(), useInstallation: vi.fn() }));
import { boardPixels, edgeStrips, swapStrips, twoStripEdges, unevenStripNotice, unmappedLeds, validateBoards, validateLightShow } from '../lib/installation';
import type { Board, InstallationSettings } from '../lib/contracts';

const board = (): Board => ({ id: 'a', name: 'Board 1', deviceMac: '001122334455', rotation: 0, edges: twoStripEdges(16) });
const settings = (boards: Board[]): InstallationSettings => ({ version: 1, boards,
  devices: [{ mac: '001122334455', name: 'WLED', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
  controllers: [], idleSync: { enabled: false, masterMac: null, followers: [], group: 1 },
  animation: { framesPerSecond: 30, flashSeconds: 2, dotSeconds: 3, dotLapsPerSecond: 4 },
  lightShow: { theme: 'neonSunset', brightness: 100, speed: 100, intensity: 'calm',
    custom: { primary: '#ff4fad', secondary: '#ffad66', tertiary: '#8c50ff', accent: '#96eddf' } } });

describe('two strips starting at the same corner', () => {
  it('maps both outputs in opposite directions and covers each LED exactly once', () => {
    const pixels = boardPixels(board());
    expect(pixels.map(p => p.index)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 15, 14, 13, 12, 11, 10, 9, 8]);
    expect(new Set(pixels.map(p => p.index)).size).toBe(16);
    expect(() => twoStripEdges(2)).toThrow('at least four LEDs');
    expect(() => twoStripEdges(3.5)).toThrow();
  });
  it('colors the two strips apart and marks where each starts, in every rotation and after a swap', () => {
    const mapped = board();
    // Template: strip 1 is top then right, strip 2 is left then bottom.
    expect(edgeStrips(mapped.edges)).toEqual([0, 0, 1, 1]);
    for (const rotation of [0, 90, 180, 270]) {
      mapped.rotation = rotation;
      const pixels = boardPixels(mapped);
      expect(pixels.filter(p => p.strip === 0).map(p => p.index).sort((a, b) => a - b)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
      expect(pixels.filter(p => p.stripStart).map(p => p.index).sort((a, b) => a - b)).toEqual([0, 8]);
    }
    mapped.rotation = 0;
    swapStrips(mapped);
    // Swapped, LED 0 runs down the left side and on along the bottom.
    expect(edgeStrips(mapped.edges)).toEqual([1, 1, 0, 0]);
    expect(boardPixels(mapped).filter(p => p.strip === 0).map(p => p.index).sort((a, b) => a - b)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
  });
  it('treats one strip around the board as one, and long edges keep their end LEDs', () => {
    expect(edgeStrips([0, 4, 8, 12].map(start => ({ start, count: 4, reversed: false })))).toEqual([0, 0, 0, 0]);
    // Numbers that carry on but jump to another corner start a new strip: the reversed right edge starts at the
    // bottom-right and ends at the top-right, so it joins neither the top nor the bottom edge.
    expect(edgeStrips([{ start: 0, count: 4, reversed: false }, { start: 4, count: 4, reversed: true }, { start: 8, count: 4, reversed: false }, { start: 12, count: 4, reversed: false }]))
      .toEqual([0, 1, 2, 2]);
    const long: Board = { ...board(), edges: twoStripEdges(400) };
    const pixels = boardPixels(long);
    expect(pixels).toHaveLength(256);
    expect(pixels.filter(p => p.stripStart).map(p => p.index).sort((a, b) => a - b)).toEqual([0, 200]);
    expect(unmappedLeds({ ...board(), edges: twoStripEdges(17) }, 17)).toBe(1);
    expect(unmappedLeds(board(), 16)).toBe(0);
  });
  it('maps the largest multiple of four and says which LEDs stay dark when the total does not divide', () => {
    // A 17-LED controller: 16 are mapped exactly as for 16, the last one is left out.
    expect(twoStripEdges(17)).toEqual(twoStripEdges(16));
    expect(twoStripEdges(19)).toEqual(twoStripEdges(16));
    const uneven: Board = { ...board(), edges: twoStripEdges(17) };
    const devices = settings([uneven]);
    devices.devices[0]!.ledCount = 17;
    expect(() => validateBoards(devices)).not.toThrow();
    expect(new Set(boardPixels(uneven).map(p => p.index))).toEqual(new Set(Array.from({ length: 16 }, (_, i) => i)));
    expect(unevenStripNotice('WLED', 16)).toBe('');
    expect(unevenStripNotice('WLED', 17)).toContain('The first 16 are mapped and the last one stays dark');
    expect(unevenStripNotice('WLED', 19)).toContain('The first 16 are mapped and the last 3 stay dark');
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
    expect(() => validateBoards(settings([{ ...slot, deviceMac: '001122334455' }]))).toThrow('its LEDs are not mapped yet');
    expect(() => validateBoards(settings([{ ...board(), edges: board().edges.slice(0, 3) }]))).toThrow('four mapped edges');
    const overlapping = board(); overlapping.edges[1]!.start = 3;
    expect(() => validateBoards(settings([overlapping]))).toThrow('overlap');
  });
});

describe('light show draft', () => {
  const draft = () => structuredClone(settings([]));
  it('accepts the defaults and every built-in limit', () => {
    const { lightShow, animation } = draft();
    expect(() => validateLightShow(lightShow, animation)).not.toThrow();
    expect(() => validateLightShow({ ...lightShow, brightness: 20, speed: 200, intensity: 'lively' }, animation)).not.toThrow();
  });
  it('rejects values the server would refuse before they reach the boards', () => {
    const { lightShow, animation } = draft();
    expect(() => validateLightShow({ ...lightShow, brightness: 19 }, animation)).toThrow('Brightness');
    expect(() => validateLightShow({ ...lightShow, brightness: 50.5 }, animation)).toThrow('Brightness');
    expect(() => validateLightShow({ ...lightShow, speed: 201 }, animation)).toThrow('Drift speed');
    expect(() => validateLightShow(lightShow, { ...animation, framesPerSecond: 9 })).toThrow('Frames per second');
    expect(() => validateLightShow(lightShow, { ...animation, flashSeconds: 0.5 })).toThrow('Flare seconds');
    expect(() => validateLightShow(lightShow, { ...animation, dotSeconds: Number.NaN })).toThrow('Comet seconds');
    expect(() => validateLightShow(lightShow, { ...animation, dotLapsPerSecond: 21 })).toThrow('Comet laps');
  });
  it('checks custom colors only when the custom theme is chosen', () => {
    const { lightShow, animation } = draft();
    const black = { ...lightShow.custom, accent: '#000000' };
    expect(() => validateLightShow({ ...lightShow, custom: black }, animation)).not.toThrow();
    expect(() => validateLightShow({ ...lightShow, theme: 'custom', custom: black }, animation)).toThrow('black');
    expect(() => validateLightShow({ ...lightShow, theme: 'custom', custom: { ...lightShow.custom, primary: 'red' } }, animation)).toThrow('#RRGGBB');
    expect(() => validateLightShow({ ...lightShow, theme: 'custom' }, animation)).not.toThrow();
  });
});
