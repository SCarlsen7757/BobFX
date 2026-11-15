import { describe, expect, it, vi } from 'vitest';
vi.mock('../lib/session', () => ({ connected: { value: true }, useEffect: vi.fn(), useGame: vi.fn(), useInstallation: vi.fn() }));
import { boardProblems, defaultStrips, describeProblem, idleSyncProblems, ledCountNotice, resetMapping, reverseStrip, stripRange, swapSides, turnBoard,
  unmappedLeds, validateLightShow } from '../lib/installation';
import { boardDots, boardTile } from '../lib/boardDrawing';
import type { Board, IdleSync, InstallationSettings, SavedDevice } from '../lib/contracts';

const device = (ledCount = 102, mac = '001122334455'): SavedDevice =>
  ({ mac, name: 'WLED', address: '192.168.1.20', httpPort: 80, ledCount, udpPort: 21324, selected: true, simulated: false });
const board = (ledCount = 102): Board => ({ id: 'a', name: 'Board 1', deviceMac: '001122334455', ...defaultStrips(ledCount)! });
const settings = (boards: Board[]): InstallationSettings => ({ version: 2, boards, devices: [device(16)],
  controllers: [], countdown: null, idleSync: { enabled: false, masterMac: null, followers: [], group: 1 },
  animation: { framesPerSecond: 30, flashSeconds: 2, dotSeconds: 3, dotLapsPerSecond: 4 },
  lightShow: { theme: 'neonSunset', brightness: 100, speed: 100, intensity: 'calm',
    custom: { primary: '#ff4fad', secondary: '#ffad66', tertiary: '#8c50ff', accent: '#96eddf' } } });

describe('a board with a strip down each side', () => {
  it('maps the first half of the controller to the left strip and the second half to the right', () => {
    expect(defaultStrips(102)).toEqual({ left: { start: 0, count: 51, reversed: false }, right: { start: 51, count: 51, reversed: false } });
    // An odd LED at the end stays dark; fewer than two LEDs cannot make two strips.
    expect(defaultStrips(17)).toEqual({ left: { start: 0, count: 8, reversed: false }, right: { start: 8, count: 8, reversed: false } });
    expect(defaultStrips(1)).toBeNull();
    expect(defaultStrips(3.5)).toBeNull();
    expect(unmappedLeds(board(17), 17)).toBe(1);
    expect(unmappedLeds(board(), 102)).toBe(0);
    expect(ledCountNotice(device(102))).toBe('');
    expect(ledCountNotice(device(17))).toBe('WLED reports 17 LEDs, not 102.');
    expect(stripRange(board().left)).toBe('LEDs 0–50');
    expect(stripRange({ start: 4, count: 1, reversed: false })).toBe('LED 4');
    expect(stripRange(null)).toBe('not mapped');
  });
  it('draws both strips from the near end at the bottom to the far end at the top', () => {
    const dots = boardDots(board());
    expect(dots).toHaveLength(102);
    const left = dots.filter(dot => dot.side === 0), right = dots.filter(dot => dot.side === 1);
    expect(left.map(dot => dot.index)).toEqual(Array.from({ length: 51 }, (_, i) => i));
    expect(right.map(dot => dot.index)).toEqual(Array.from({ length: 51 }, (_, i) => 51 + i));
    expect(left.every(dot => dot.x === boardTile.left)).toBe(true);
    expect(right.every(dot => dot.x === boardTile.right)).toBe(true);
    // LED 0 is nearest the operator; higher numbers climb the board.
    expect(left[0]!.y).toBeGreaterThan(left[50]!.y);
    expect(left[0]!.y).toBeLessThan(boardTile.bottom); expect(left[50]!.y).toBeGreaterThan(boardTile.top);
    expect(dots.filter(dot => dot.first).map(dot => dot.index)).toEqual([0, 51]);
    // A reversed strip keeps its place but its first LED is at the far end.
    const reversed = board(); reverseStrip(reversed, 'right');
    const turned = boardDots(reversed).filter(dot => dot.side === 1);
    expect(turned[0]!.index).toBe(101); expect(turned[50]!.index).toBe(51); expect(turned[50]!.first).toBe(true);
    // Long strips are thinned but keep both end LEDs; an unmapped or broken strip draws nothing.
    const long = boardDots({ left: { start: 0, count: 400, reversed: false }, right: null });
    expect(long).toHaveLength(80); expect(long[0]!.index).toBe(0); expect(long[79]!.index).toBe(399);
    expect(boardDots({ left: null, right: { start: 0, count: Number.NaN, reversed: false } })).toEqual([]);
  });
  it('corrects the wiring with one action each, without losing LEDs', () => {
    const mapped = board();
    swapSides(mapped);
    expect([mapped.left!.start, mapped.right!.start]).toEqual([51, 0]);
    reverseStrip(mapped, 'left');
    expect([mapped.left!.reversed, mapped.right!.reversed]).toEqual([true, false]);
    resetMapping(mapped, 102);
    expect(mapped).toEqual(board());
    // Turning the board round swaps the sides and runs both strips the other way; twice is back to the start.
    turnBoard(mapped);
    expect(mapped.left).toEqual({ start: 51, count: 51, reversed: true });
    expect(mapped.right).toEqual({ start: 0, count: 51, reversed: true });
    expect(new Set(boardDots(mapped).map(dot => dot.index)).size).toBe(102);
    turnBoard(mapped);
    expect(mapped).toEqual(board());
    resetMapping(mapped, 1);
    expect([mapped.left, mapped.right]).toEqual([null, null]);
  });
  it('accepts unassigned slots and reports each problem against its board and field', () => {
    const slot: Board = { id: 'slot1', name: 'Waiting', deviceMac: null, left: null, right: null };
    const devices = [device(16)];
    expect(boardProblems([slot, board(16), { ...slot, id: 'slot2' }], devices)).toEqual([]);

    const twice = boardProblems([board(16), { ...board(16), id: 'b', name: 'Board 2' }], devices);
    expect(twice).toEqual([{ boardId: 'b', side: undefined, part: undefined, message: 'WLED is already used by Board 1. A controller drives one board.' }]);

    expect(boardProblems([{ ...slot, deviceMac: '001122334455' }], devices).map(problem => [problem.side, problem.message])).toEqual([
      ['left', 'The left strip is not mapped. Use Reset mapping.'], ['right', 'The right strip is not mapped. Use Reset mapping.']]);
    expect(boardProblems([{ ...board(16), deviceMac: 'aabbccddeeff' }], devices)[0]!.message).toContain('no longer registered or selected');
    expect(boardProblems([{ ...board(16), name: ' ' }], devices)[0]!.message).toBe('Give the board a name.');

    // The standard 51 + 51 mapping on a controller that reports 16 LEDs: both strips say what does not fit.
    const tooLong = boardProblems([board(102)], devices);
    expect(tooLong.map(problem => [problem.side, problem.part])).toEqual([['left', 'count'], ['right', 'count']]);
    expect(tooLong[1]!.message).toBe('The right strip uses LEDs 51–101, but WLED has 16 LEDs (0–15).');
    expect(describeProblem(tooLong[1]!, [board(102)])).toBe('Board 1: The right strip uses LEDs 51–101, but WLED has 16 LEDs (0–15).');

    const overlapping = board(16); overlapping.right!.start = 7;
    expect(boardProblems([overlapping], devices).map(problem => problem.message)).toEqual(['The left strip (LEDs 0–7) and the right strip (LEDs 7–14) use the same LEDs.']);

    // A cleared number field holds '' and a count of 0 is not a strip: each is reported on its own field.
    const empty = board(16); (empty.left as unknown as { start: unknown }).start = ''; empty.right!.count = 0;
    expect(boardProblems([empty], devices).map(problem => [problem.side, problem.part, problem.message])).toEqual([
      ['left', 'start', 'The first LED must be a whole number, 0 or higher.'], ['right', 'count', 'The LED count must be a whole number, 1 or higher.']]);
  });
  it('checks idle sync before it is sent', () => {
    const devices = [device(102), { ...device(102, 'aabbccddeeff'), name: 'Second' }];
    const sync: IdleSync = { enabled: false, masterMac: null, followers: [], group: 1 };
    expect(idleSyncProblems(sync, devices)).toEqual([]);
    expect(idleSyncProblems({ ...sync, enabled: true }, devices)).toEqual(['Idle sync: choose a master, or turn idle sync off.']);
    expect(idleSyncProblems({ ...sync, group: '' as unknown as number }, devices)[0]).toContain('whole number from 1 to 8');
    expect(idleSyncProblems({ ...sync, group: 9 }, devices)).toHaveLength(1);
    expect(idleSyncProblems({ ...sync, followers: ['ffffffffffff'] }, devices)[0]).toContain('no longer registered');
    const enabled = { ...sync, enabled: true, masterMac: '001122334455', followers: ['aabbccddeeff'] };
    expect(idleSyncProblems(enabled, devices)).toEqual([]);
    expect(idleSyncProblems(enabled, [devices[0]!, { ...devices[1]!, udpPort: 21325 }])[0]).toContain('same WLED UDP port');
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
  it('checks custom colors whatever the theme, as the server does', () => {
    const { lightShow, animation } = draft();
    const black = { ...lightShow.custom, accent: '#000000' };
    expect(() => validateLightShow({ ...lightShow, custom: black }, animation)).toThrow('black');
    expect(() => validateLightShow({ ...lightShow, theme: 'custom', custom: black }, animation)).toThrow('black');
    expect(() => validateLightShow({ ...lightShow, theme: 'custom', custom: { ...lightShow.custom, primary: 'red' } }, animation)).toThrow('#RRGGBB');
    expect(() => validateLightShow({ ...lightShow, theme: 'custom' }, animation)).not.toThrow();
  });
});
