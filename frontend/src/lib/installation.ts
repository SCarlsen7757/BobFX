import { computed } from 'vue';
import { connected, useEffect, useGame, useInstallation } from './session';
import type { AnimationSettings, Board, Edge, InstallationSettings, LightShowSettings } from './contracts';

export function clone<T>(value: T): T { return JSON.parse(JSON.stringify(value)) as T; }
export function newId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join(''); }
/** True while nothing else owns the lights. A light show test may replace a running test, so it passes `duringLightTest`. */
export function useCanConfigure(duringLightTest = false) {
  const game = useGame(), effect = useEffect(), installation = useInstallation();
  return computed(() => connected.value && game.data.value?.value.phase === 'Idle' &&
    !effect.data.value?.value.streaming && !installation.data.value?.value.locating && !installation.data.value?.value.calibration &&
    (duringLightTest || !installation.data.value?.value.lightShowTest));
}
// Mirrors the server's limits so a draft is rejected before it is sent to the boards.
export function validateLightShow(show: LightShowSettings, animation: AnimationSettings) {
  const between = (value: number, low: number, high: number) => Number.isFinite(value) && value >= low && value <= high;
  if (!Number.isInteger(show.brightness) || !between(show.brightness, 20, 100)) throw new Error('Brightness must be 20–100%.');
  if (!Number.isInteger(show.speed) || !between(show.speed, 25, 200)) throw new Error('Drift speed must be 25–200%.');
  if (!['calm', 'balanced', 'lively'].includes(show.intensity)) throw new Error('Choose an intensity.');
  if (show.theme === 'custom')
    for (const color of [show.custom.primary, show.custom.secondary, show.custom.tertiary, show.custom.accent]) {
      if (!/^#[0-9a-f]{6}$/i.test(color)) throw new Error('Custom colors must be #RRGGBB.');
      if (Math.max(...[1, 3, 5].map(at => parseInt(color.slice(at, at + 2), 16))) < 0x30) throw new Error('Custom colors must not be black or nearly black.');
    }
  if (!Number.isInteger(animation.framesPerSecond) || !between(animation.framesPerSecond, 10, 60)) throw new Error('Frames per second must be 10–60.');
  if (!between(animation.flashSeconds, 1, 10)) throw new Error('Flare seconds must be 1–10.');
  if (!between(animation.dotSeconds, 1, 30)) throw new Error('Comet seconds must be 1–30.');
  if (!between(animation.dotLapsPerSecond, 1, 20)) throw new Error('Comet laps per second must be 1–20.');
}
// Both strips start at top-left: output 1 follows top/right, output 2 follows left/bottom.
// A total that does not divide by four maps the largest multiple of four; the LEDs left over at the end stay dark.
export function twoStripEdges(ledCount: number): Edge[] {
  if (!Number.isInteger(ledCount) || ledCount < 4 || ledCount > 65535)
    throw new Error('A board needs at least four LEDs. Scan again in WLED devices if the controller reports the wrong total.');
  const count = Math.floor(ledCount / 4);
  return [{ start: 0, count, reversed: false }, { start: count, count, reversed: false },
    { start: count * 3, count, reversed: true }, { start: count * 2, count, reversed: true }];
}
/** What the two-strip mapping left out for a controller whose LED total does not divide by four; empty when it does. */
export function unevenStripNotice(name: string, ledCount: number) {
  const spare = ledCount % 4;
  return spare === 0 ? '' : `${name} reports ${ledCount} LEDs, which do not divide into four equal edges. The first ${ledCount - spare} are mapped and the last ${spare === 1 ? 'one stays' : `${spare} stay`} dark. If the strips are wired differently, change the edge ranges under Advanced settings.`;
}
export function swapStrips(board: Board) {
  // Swap physical strip routes without assuming contiguous or equally sized ranges.
  board.edges = [3, 2, 1, 0].map(side => ({ ...board.edges[side]!, reversed: !board.edges[side]!.reversed }));
}
export function validateBoards(settings: InstallationSettings) {
  const ids = new Set<string>(), devices = new Set<string>();
  for (const board of settings.boards) {
    const device = settings.devices.find(d => d.mac === board.deviceMac && d.selected);
    if (board.deviceMac !== null && board.edges.length === 0)
      throw new Error(`${board.name}: its LEDs are not mapped yet. Use the two-strip template or set the mapping manually under Advanced settings.`);
    if (!board.id || !board.name.trim() || ![0, 90, 180, 270].includes(board.rotation) ||
      (board.edges.length !== 4 && !(board.deviceMac === null && board.edges.length === 0)))
      throw new Error('Boards require a name, rotation 0/90/180/270 and four mapped edges when assigned.');
    if (board.deviceMac !== null && !device) throw new Error(`${board.name}: choose a selected WLED controller.`);
    if (ids.has(board.id) || (board.deviceMac !== null && devices.has(board.deviceMac))) throw new Error('Each board needs a unique ID and WLED controller.');
    ids.add(board.id); if (board.deviceMac !== null) devices.add(board.deviceMac);
    const used = new Set<number>();
    for (const edge of board.edges) {
      if (!Number.isInteger(edge.start) || !Number.isInteger(edge.count) || edge.start < 0 || edge.count < 1 || edge.start + edge.count > (device?.ledCount ?? 65535))
        throw new Error(`${board.name}: edge lies outside the device LED count.`);
      for (let pixel = edge.start; pixel < edge.start + edge.count; pixel++) {
        if (used.has(pixel)) throw new Error(`${board.name}: edge mappings overlap.`);
        used.add(pixel);
      }
    }
  }
}
/**
 * Which strip each edge belongs to, numbered from the strip holding the lowest LED index. Only the edge ranges are
 * saved, so strips are inferred: an edge continues the previous one when its LED numbers carry on and its first LED
 * sits at the corner where the previous edge's last LED is. Edges run clockwise from corner to corner (top-left = 0);
 * an edge's first LED is at its start corner unless the edge is reversed. Unmapped edges get -1.
 */
export function edgeStrips(edges: Edge[]): number[] {
  const strips = edges.map(() => -1);
  const order = edges.map((_, side) => side).filter(side => edges[side]!.count >= 1).sort((a, b) => edges[a]!.start - edges[b]!.start);
  let strip = -1, previous: number | undefined;
  for (const side of order) {
    const edge = edges[side]!, before = previous === undefined ? undefined : edges[previous]!;
    const lastCorner = before && (before.reversed ? previous! : (previous! + 1) % 4);
    const firstCorner = edge.reversed ? (side + 1) % 4 : side;
    if (!before || before.start + before.count !== edge.start || lastCorner !== firstCorner) strip++;
    strips[side] = strip; previous = side;
  }
  return strips;
}
/** LEDs of a controller that no edge covers, such as the spare when the total does not divide by four. */
export function unmappedLeds(board: Board, ledCount: number) {
  return Math.max(0, ledCount - board.edges.reduce((sum, edge) => sum + Math.max(0, edge.count), 0));
}
export function boardPixels(board: Board) {
  const pixels: Array<{ x: number; y: number; index: number; strip: number; stripStart: boolean }> = [];
  const strips = edgeStrips(board.edges);
  // The first LED of each strip, for the start marker.
  const starts = new Set(board.edges.filter((_, side) => strips[side]! >= 0 && !board.edges.some((other, o) => strips[o] === strips[side] && other.start < board.edges[side]!.start))
    .map(edge => edge.start));
  for (let side = 0; side < 4; side++) {
    const physical = (side - board.rotation / 90 + 4) % 4;
    const edge = board.edges[physical];
    if (!edge || edge.count < 1 || edge.count > 65535) continue;
    const count = Math.min(64, edge.count);
    for (let i = 0; i < count; i++) {
      const fraction = (i + .5) / count;
      // Long edges are thinned to 64 dots; keep both end LEDs so a strip's first LED is always drawn.
      const offset = count === edge.count ? i : Math.round(i * (edge.count - 1) / (count - 1));
      const index = edge.start + (edge.reversed ? edge.count - 1 - offset : offset);
      pixels.push({ x: side === 0 ? 10 + fraction * 180 : side === 1 ? 190 : side === 2 ? 190 - fraction * 180 : 10,
        y: side === 0 ? 10 : side === 1 ? 10 + fraction * 180 : side === 2 ? 190 : 190 - fraction * 180,
        index, strip: strips[physical]!, stripStart: starts.has(index) });
    }
  }
  return pixels;
}
