import { computed } from 'vue';
import { connected, useEffect, useGame, useInstallation } from './session';
import type { Board, InstallationSettings } from './contracts';

export function clone<T>(value: T): T { return JSON.parse(JSON.stringify(value)) as T; }
export function newId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join(''); }
export function useCanConfigure() {
  const game = useGame(), effect = useEffect(), installation = useInstallation();
  return computed(() => connected.value && game.data.value?.value.phase === 'Idle' &&
    !effect.data.value?.value.streaming && !installation.data.value?.value.locating);
}
export function validateBoards(settings: InstallationSettings) {
  const ids = new Set<string>(), devices = new Set<string>();
  for (const board of settings.boards) {
    const device = settings.devices.find(d => d.mac === board.deviceMac && d.selected);
    if (!board.name.trim() || !device || ![0, 90, 180, 270].includes(board.rotation) || board.edges.length !== 4)
      throw new Error('Boards require a name, selected device, four edges and rotation 0/90/180/270.');
    if (ids.has(board.id) || devices.has(board.deviceMac)) throw new Error('Each board needs a unique WLED device.');
    ids.add(board.id); devices.add(board.deviceMac);
    const used = new Set<number>();
    for (const edge of board.edges) {
      if (!Number.isInteger(edge.start) || !Number.isInteger(edge.count) || edge.start < 0 || edge.count < 1 || edge.start + edge.count > device.ledCount)
        throw new Error(`${board.name}: edge lies outside the device LED count.`);
      for (let pixel = edge.start; pixel < edge.start + edge.count; pixel++) {
        if (used.has(pixel)) throw new Error(`${board.name}: edge mappings overlap.`);
        used.add(pixel);
      }
    }
  }
}
export function boardPixels(board: Board) {
  const pixels: Array<{ x: number; y: number; index: number; playerEdge: boolean }> = [];
  for (let side = 0; side < 4; side++) {
    const edge = board.edges[(side - board.rotation / 90 + 4) % 4];
    if (!edge || edge.count < 1 || edge.count > 65535) continue;
    const count = Math.min(64, edge.count);
    for (let i = 0; i < count; i++) {
      const fraction = (i + .5) / count;
      const offset = Math.floor(i * edge.count / count);
      pixels.push({ x: side === 0 ? 10 + fraction * 180 : side === 1 ? 190 : side === 2 ? 190 - fraction * 180 : 10,
        y: side === 0 ? 10 : side === 1 ? 10 + fraction * 180 : side === 2 ? 190 : 190 - fraction * 180,
        index: edge.start + (edge.reversed ? edge.count - 1 - offset : offset), playerEdge: side % 2 === 0 });
    }
  }
  return pixels;
}
