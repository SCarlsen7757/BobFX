import type { Strip } from './contracts';

/**
 * One drawing of a board for every page: an upright board seen from above with the near end at the bottom, a strip
 * of LEDs down its left side and one down its right. Board layout draws every LED as a dot in SVG, because there
 * the numbering matters; the light show preview and the live view paint the light itself on a canvas with
 * `paintBoards`, as a border of blended color down each long side of the board.
 */
export const boardTile = { width: 150, height: 290, left: 27, right: 123, top: 16, bottom: 252, label: 276 };

export interface BoardDot { x: number; y: number; index: number; side: 0 | 1; first: boolean }

/** Where a board's LEDs are drawn, from the near end (bottom) to the far end (top) of each strip. */
export function boardDots(board: { left: Strip | null; right: Strip | null }): BoardDot[] {
  const dots: BoardDot[] = [];
  const { left, right, top, bottom } = boardTile;
  ([board.left, board.right] as const).forEach((strip, side) => {
    if (!strip || !Number.isInteger(strip.start) || !Number.isInteger(strip.count) || strip.start < 0 || strip.count < 1 || strip.count > 65535) return;
    // Long strips are thinned to 80 dots; both end LEDs are kept so a strip's first LED is always drawn.
    const count = Math.min(80, strip.count);
    for (let i = 0; i < count; i++) {
      const offset = count === strip.count ? i : Math.round(i * (strip.count - 1) / (count - 1));
      const index = strip.start + (strip.reversed ? strip.count - 1 - offset : offset);
      dots.push({ x: side === 0 ? left : right, y: bottom - (i + .5) / count * (bottom - top), index, side: side as 0 | 1, first: index === strip.start });
    }
  });
  return dots;
}

export interface PaintedBoard {
  label: string;
  /** Dashed outline, as for a board without a controller. */
  dashed?: boolean;
  /** Text across the middle of the board, such as "Idle". */
  note?: string;
  dots: BoardDot[];
  /** RGB bytes holding this board's LEDs from `offset` on; null leaves the board's border unlit. */
  pixels: Uint8Array | null;
  offset?: number;
}

// LEDs emit linear light; a screen expects gamma-encoded values, so frames are encoded back before drawing.
const toScreen = Uint8Array.from({ length: 256 }, (_, v) => Math.round(255 * Math.pow(v / 255, 1 / 2.2)));

// How wide the lit border is, and the wider, fainter passes under it that stand in for the glow of the LEDs.
// Several passes fade the glow out in steps; a canvas blur filter would be smoother but Safari does not have one.
const borderWidth = 7;
const borderPasses = [{ width: borderWidth + 20, alpha: .06 }, { width: borderWidth + 13, alpha: .09 }, { width: borderWidth + 6, alpha: .15 },
  { width: borderWidth, alpha: 1 }];

interface StripLight { image: HTMLCanvasElement; rows: number }
// One reusable image per side. Each is a single pixel wide with a row per LED: stretched along a side of the
// board with smoothing, neighbouring LEDs run into each other as a gradient.
const stripImages: Array<HTMLCanvasElement | undefined> = [undefined, undefined];
function stripLight(side: 0 | 1, dots: BoardDot[], frame: Uint8Array, offset: number): StripLight | undefined {
  const strip = dots.filter(dot => dot.side === side);
  if (!strip.length) return undefined;
  const image = stripImages[side] ??= document.createElement('canvas');
  if (image.width !== 1 || image.height !== strip.length) { image.width = 1; image.height = strip.length; }
  const context = image.getContext('2d');
  if (!context) return undefined;
  const data = context.createImageData(1, strip.length);
  strip.forEach((dot, row) => {
    // Dots run from the near end at the bottom, image rows from the top.
    const at = (offset + dot.index) * 3, pixel = (strip.length - 1 - row) * 4;
    data.data[pixel] = toScreen[frame[at] ?? 0]!; data.data[pixel + 1] = toScreen[frame[at + 1] ?? 0]!;
    data.data[pixel + 2] = toScreen[frame[at + 2] ?? 0]!; data.data[pixel + 3] = 255;
  });
  context.putImageData(data, 0, 0);
  return { image, rows: strip.length };
}

/**
 * Paints boards side by side in row order. The light is on the two long borders of the board, where the strips
 * are: the left strip's colors run up its left side and the right strip's up its right side, each blended along
 * its length into a gradient. The near and far ends have no LEDs and stay unlit, as on a real board. Only the
 * step from LED light to a screen is approximate.
 */
export function paintBoards(canvas: HTMLCanvasElement, boards: PaintedBoard[]) {
  const scale = window.devicePixelRatio || 1, width = Math.max(1, boards.length) * boardTile.width, height = boardTile.height;
  if (canvas.width !== Math.round(width * scale) || canvas.height !== Math.round(height * scale)) {
    canvas.width = Math.round(width * scale); canvas.height = Math.round(height * scale);
  }
  const context = canvas.getContext('2d');
  if (!context) return;
  context.setTransform(scale, 0, 0, scale, 0, 0);
  context.fillStyle = '#0b0614'; context.fillRect(0, 0, width, height);
  const { left, right, top, bottom, label } = boardTile;
  boards.forEach((board, position) => {
    const x = position * boardTile.width;
    // The board itself: a dark surface with a faint outline, which is all an unlit board shows.
    context.fillStyle = '#130b1f'; context.fillRect(x + left, top, right - left, bottom - top);
    context.strokeStyle = '#3a2648'; context.lineWidth = 2; context.setLineDash(board.dashed ? [6, 5] : []);
    context.strokeRect(x + left, top, right - left, bottom - top); context.setLineDash([]);
    if (board.pixels) {
      const sides = [stripLight(0, board.dots, board.pixels, board.offset ?? 0), stripLight(1, board.dots, board.pixels, board.offset ?? 0)];
      // Light is added to what is there, so an LED that is off leaves the faint outline in place.
      context.imageSmoothingEnabled = true;
      context.globalCompositeOperation = 'lighter';
      for (const pass of borderPasses) {
        context.globalAlpha = pass.alpha;
        const half = pass.width / 2;
        sides.forEach((side, index) => {
          if (side) context.drawImage(side.image, 0, 0, 1, side.rows, x + (index === 0 ? left : right) - half, top, pass.width, bottom - top);
        });
      }
      context.globalAlpha = 1; context.globalCompositeOperation = 'source-over';
    }
    context.textAlign = 'center'; context.font = '12px "Space Grotesk Variable", sans-serif';
    context.fillStyle = '#c0adcd'; context.fillText(board.label, x + boardTile.width / 2, label, boardTile.width - 8);
    context.fillStyle = '#7d6a8c';
    if (board.note) context.fillText(board.note, x + boardTile.width / 2, (top + bottom) / 2, right - left - 24);
  });
}
/** Inline size for a canvas painted by `paintBoards`, so it keeps its shape while it scales down on a narrow screen. */
export function canvasShape(boards: number) {
  const count = Math.max(1, boards);
  return { aspectRatio: `${count * boardTile.width} / ${boardTile.height}`, maxWidth: `${count * boardTile.width}px` };
}
