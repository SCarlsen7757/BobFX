import { computed } from 'vue';
import { connected, useEffect, useGame, useInstallation } from './session';
import { maxLeds, wellFormed } from './strips';
import type { AnimationSettings, Board, IdleSync, LightShowSettings, SavedDevice, Strip } from './contracts';

export { maxLeds, wellFormed };

export function clone<T>(value: T): T { return JSON.parse(JSON.stringify(value)) as T; }
export function newId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join(''); }
export function isIntBetween(value: number, low: number, high: number) { return Number.isInteger(value) && value >= low && value <= high; }

/** The installation as the server last reported it, in the parts the pages read. */
export function useInstallationState() {
  const { data } = useInstallation();
  const state = computed(() => data.value?.value);
  return {
    state,
    settings: computed(() => state.value?.settings),
    devices: computed<SavedDevice[]>(() => state.value?.settings.devices ?? []),
    /** What went wrong the last time a frame was sent to a controller, by MAC. */
    outputErrors: computed(() => state.value?.outputErrors ?? {}),
    controllerStatus: computed(() => state.value?.controllers ?? {}),
    passwordsSaved: computed(() => state.value?.passwordsSaved ?? {}),
    calibration: computed(() => state.value?.calibration ?? null),
    lightShowTest: computed(() => state.value?.lightShowTest ?? null),
  };
}

/** Who has the lights. The server lets one of these at a time send frames; everything else has to wait for it. */
export type LightsOwner = 'calibration' | 'lightTest' | 'locating' | 'game' | 'manual';
export function useLightsOwner() {
  const game = useGame(), effect = useEffect(), { state } = useInstallationState();
  return computed<LightsOwner | null>(() =>
    state.value?.calibration ? 'calibration' : state.value?.lightShowTest ? 'lightTest' : state.value?.locating ? 'locating'
    // A game keeps the output streaming, so it is checked before the manual effect that streaming otherwise means.
    : (game.data.value?.value.phase ?? 'Idle') !== 'Idle' ? 'game' : effect.data.value?.value.streaming ? 'manual' : null);
}
/** True while nothing else owns the lights. A light show test may replace a running test, so it passes `duringLightTest`. */
export function useCanConfigure(duringLightTest = false) {
  const game = useGame(), owner = useLightsOwner();
  return computed(() => connected.value && !!game.data.value &&
    (owner.value === null || (duringLightTest && owner.value === 'lightTest')));
}

// Mirrors the server's limits so a draft is rejected before it is sent to the boards.
export function validateLightShow(show: LightShowSettings, animation: AnimationSettings) {
  const between = (value: number, low: number, high: number) => Number.isFinite(value) && value >= low && value <= high;
  if (!isIntBetween(show.brightness, 20, 100)) throw new Error('Brightness must be 20–100%.');
  if (!isIntBetween(show.speed, 25, 200)) throw new Error('Drift speed must be 25–200%.');
  if (!['calm', 'balanced', 'lively'].includes(show.intensity)) throw new Error('Choose an intensity.');
  // Checked whatever the theme: the server stores the custom colors with every save and refuses unusable ones.
  for (const color of [show.custom.primary, show.custom.secondary, show.custom.tertiary, show.custom.accent]) {
    if (!/^#[0-9a-f]{6}$/i.test(color)) throw new Error('Custom colors must be #RRGGBB.');
    if (Math.max(...[1, 3, 5].map(at => parseInt(color.slice(at, at + 2), 16))) < 0x30) throw new Error('Custom colors must not be black or nearly black.');
  }
  if (!isIntBetween(animation.framesPerSecond, 10, 60)) throw new Error('Frames per second must be 10–60.');
  if (!between(animation.flashSeconds, 1, 10)) throw new Error('Flare seconds must be 1–10.');
  if (!between(animation.dotSeconds, 1, 30)) throw new Error('Comet seconds must be 1–30.');
  if (!between(animation.dotLapsPerSecond, 1, 20)) throw new Error('Comet laps per second must be 1–20.');
}

/** A standard board: a strip of 51 LEDs down the left side and one down the right. */
const ledsPerStrip = 51, ledsPerBoard = ledsPerStrip * 2;
export type Side = 'left' | 'right';
export const sides: Side[] = ['left', 'right'];
export const sideNames: Record<Side, string> = { left: 'Left', right: 'Right' };

/**
 * The mapping a board gets when a controller is assigned: the first half of its LEDs on the left strip and the
 * second half on the right, both starting at the near end. An odd LED at the end stays dark. Null when the
 * controller has fewer than two LEDs. Mirrors BoardMapping.Default on the server.
 */
export function defaultStrips(ledCount: number): { left: Strip; right: Strip } | null {
  const half = Number.isInteger(ledCount) ? Math.floor(ledCount / 2) : 0;
  return half < 1 ? null : { left: { start: 0, count: half, reversed: false }, right: { start: half, count: half, reversed: false } };
}
export function resetMapping(board: Board, ledCount: number) {
  const strips = defaultStrips(ledCount);
  board.left = strips?.left ?? null; board.right = strips?.right ?? null;
}
/** The strips are plugged into each other's outputs: what was mapped as left is on the right. */
export function swapSides(board: Board) { [board.left, board.right] = [board.right, board.left]; }
/** The strip is wired from the other end. */
export function reverseStrip(board: Board, side: Side) { const strip = board[side]; if (strip) strip.reversed = !strip.reversed; }
/** The board stands the other way round: the strips change sides and both run the other way. */
export function turnBoard(board: Board) { swapSides(board); for (const side of sides) reverseStrip(board, side); }
/** Whether both strips of a board are mapped onto LEDs the controller has. */
export function fitsController(board: Board, ledCount: number) {
  return sides.every(side => { const strip = board[side]; return !!strip && wellFormed(strip) && strip.start + strip.count <= ledCount; });
}
/** "LEDs 0–50", as the strip is numbered on its controller. */
export function stripRange(strip: Strip | null) {
  if (!strip) return 'not mapped';
  if (!wellFormed(strip)) return 'needs fixing';
  return strip.count === 1 ? `LED ${strip.start}` : `LEDs ${strip.start}–${strip.start + strip.count - 1}`;
}
/** LEDs of a controller that neither strip covers, such as the spare one when the total is odd. */
export function unmappedLeds(board: Board, ledCount: number) {
  return Math.max(0, ledCount - sides.reduce((sum, side) => sum + Math.max(0, board[side]?.count ?? 0), 0));
}
/** What to tell the operator when a controller does not report the LEDs of a standard board; empty when it does. */
export function ledCountNotice(device: SavedDevice) {
  if (device.ledCount === ledsPerBoard) return '';
  // The board's tile shows how the LEDs were split, so the notice only says what is unusual.
  return `${device.name} reports ${device.ledCount} LEDs, not ${ledsPerBoard}.`;
}

/** Something that stops a layout from being saved, tied to the board and field it belongs to. */
export interface BoardProblem { boardId: string; side?: Side; part?: 'start' | 'count'; bothStrips?: true; message: string }

/**
 * Everything wrong with a layout draft, mirroring the server's checks, so each problem is shown next to its field
 * while editing instead of in a banner after saving. Messages do not repeat the board name: `describeProblem` adds it.
 */
export function boardProblems(boards: Board[], devices: SavedDevice[]): BoardProblem[] {
  const problems: BoardProblem[] = [];
  const owners = new Map<string, Board>();
  for (const board of boards) {
    const add = (message: string, side?: Side, part?: 'start' | 'count') => problems.push({ boardId: board.id, side, part, message });
    if (!board.name.trim()) add('Give the board a name.');
    const device = devices.find(d => d.mac === board.deviceMac && d.selected);
    if (board.deviceMac !== null) {
      if (!device) add('Its WLED controller is no longer registered or selected. Choose another controller.');
      const owner = owners.get(board.deviceMac);
      if (owner) add(`${device?.name ?? 'This controller'} is already used by ${owner.name}. A controller drives one board.`);
      else owners.set(board.deviceMac, board);
    }
    for (const side of sides) {
      const strip = board[side];
      if (!strip) { if (board.deviceMac !== null) add(`The ${side} strip is not mapped. Use Reset mapping.`, side); continue; }
      if (!Number.isInteger(strip.start) || strip.start < 0) add('The first LED must be a whole number, 0 or higher.', side, 'start');
      if (!Number.isInteger(strip.count) || strip.count < 1) add('The LED count must be a whole number, 1 or higher.', side, 'count');
      if (Number.isInteger(strip.start) && Number.isInteger(strip.count) && strip.start >= 0 && strip.count >= 1 && strip.start + strip.count > (device?.ledCount ?? maxLeds))
        add(device ? `The ${side} strip uses ${stripRange(strip)}, but ${device.name} has ${device.ledCount} LEDs (0–${device.ledCount - 1}).`
          : `The ${side} strip uses ${stripRange(strip)}, which is more than a controller can have.`, side, 'count');
    }
    const { left, right } = board;
    if (left && right && wellFormed(left) && wellFormed(right) && left.start < right.start + right.count && right.start < left.start + left.count)
      problems.push({ boardId: board.id, bothStrips: true, message: `The left strip (${stripRange(left)}) and the right strip (${stripRange(right)}) use the same LEDs.` });
  }
  return problems;
}
export function describeProblem(problem: BoardProblem, boards: Board[]) {
  const name = boards.find(board => board.id === problem.boardId)?.name.trim();
  return `${name || 'Unnamed board'}: ${problem.message}`;
}
/** What is wrong with the idle sync draft; the server refuses the whole layout over any of these. */
export function idleSyncProblems(idleSync: IdleSync, devices: SavedDevice[]): string[] {
  const problems: string[] = [];
  if (!isIntBetween(idleSync.group, 1, 8)) problems.push('Idle sync: the sync group must be a whole number from 1 to 8.');
  const master = devices.find(d => d.mac === idleSync.masterMac);
  if (idleSync.enabled && !master) problems.push('Idle sync: choose a master, or turn idle sync off.');
  const followers = idleSync.followers.filter(mac => mac !== idleSync.masterMac);
  if (followers.some(mac => !devices.some(d => d.mac === mac))) problems.push('Idle sync: a follower is no longer registered. Untick it.');
  if (idleSync.enabled && master && followers.some(mac => devices.find(d => d.mac === mac)?.udpPort !== master.udpPort))
    problems.push('Idle sync: the master and its followers must use the same WLED UDP port. Set the ports in WLED first.');
  return problems;
}
