import { wellFormed } from './strips';
import type { Strip } from './contracts';

/**
 * One drawing of a board for every page: an upright board seen from above with the near end at the bottom, a strip
 * of LEDs down its left side and one down its right. Board layout draws every LED as a dot in SVG, because there
 * the numbering matters; the light show preview and the live view paint the light itself on a canvas with
 * `paintBoards`, as a border of blended color down each long side of the board.
 */
export const boardTile = { width: 150, height: 290, left: 27, right: 123, top: 16, bottom: 252, label: 276,
  /** Where Board layout writes the board's name, its two strip ranges and its unused LEDs. */
  nameY: 108, rangeY: [134, 152], spareY: 170 };

/** The two strips of a board in Board layout: the left one in the stylesheet's orange, the right one violet. */
export const stripColors = { left: '#ffad66', right: '#b18cff' };
/** On screen the calibration colors are brighter than the dim blue and amber the LEDs are sent. */
export const checkColors = { left: '#3a9ee6', right: '#e6a03a' };

export interface BoardDot { x: number; y: number; index: number; side: 0 | 1; first: boolean }
/** A board's dots by side: left, then right. */
export type BoardStrips = [BoardDot[], BoardDot[]];

/** Where a board's LEDs are drawn, from the near end (bottom) to the far end (top) of each strip. */
export function boardDots(board: { left: Strip | null; right: Strip | null }): BoardDot[] {
  const dots: BoardDot[] = [];
  const { left, right, top, bottom } = boardTile;
  ([board.left, board.right] as const).forEach((strip, side) => {
    if (!strip || !wellFormed(strip)) return;
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
export function bySide(dots: BoardDot[]): BoardStrips { return [dots.filter(dot => dot.side === 0), dots.filter(dot => dot.side === 1)]; }

export interface PaintedBoard {
  label: string;
  /** Dashed outline, as for a board without a controller. */
  dashed?: boolean;
  /** Text across the middle of the board, such as "Idle". */
  note?: string;
  strips: BoardStrips;
  /** RGB bytes holding this board's LEDs from `offset` on; null leaves the board's border unlit. */
  pixels: Uint8Array | null;
  offset?: number;
}

// The canvas cannot use the stylesheet's variables. The labels are --muted and a dimmer step of it; the rest are
// the dark surfaces of an unlit board against the page background.
const palette = { background: '#0b0614', surface: '#130b1f', outline: '#3a2648', label: '#c0adcd', note: '#7d6a8c' };
const labelFont = '12px "Space Grotesk Variable", sans-serif';

// LEDs emit linear light; a screen expects gamma-encoded values, so frames are encoded back before drawing.
const toScreen = Uint8Array.from({ length: 256 }, (_, v) => Math.round(255 * Math.pow(v / 255, 1 / 2.2)));

// How wide the lit border is, and the wider, fainter passes under it that stand in for the glow of the LEDs.
// Several passes fade the glow out in steps; a canvas blur filter would be smoother but Safari does not have one.
const borderWidth = 7;
const borderPasses = [{ width: borderWidth + 20, alpha: .06 }, { width: borderWidth + 13, alpha: .09 }, { width: borderWidth + 6, alpha: .15 },
  { width: borderWidth, alpha: 1 }];

interface StripImage { canvas: HTMLCanvasElement; context: CanvasRenderingContext2D; data: ImageData }
// One reusable image per side. Each is a single pixel wide with a row per LED: stretched along a side of the
// board with smoothing, neighbouring LEDs run into each other as a gradient. It is drawn before the next board
// fills it again, and it is only replaced when a strip has a different number of rows.
const stripImages: Array<StripImage | undefined> = [undefined, undefined];
function stripLight(side: 0 | 1, strip: BoardDot[], frame: Uint8Array, offset: number): StripImage | undefined {
  if (!strip.length) return undefined;
  let image = stripImages[side];
  if (image?.data.height !== strip.length) {
    const canvas = image?.canvas ?? document.createElement('canvas');
    canvas.width = 1; canvas.height = strip.length;
    const context = canvas.getContext('2d');
    if (!context) return undefined;
    image = stripImages[side] = { canvas, context, data: context.createImageData(1, strip.length) };
  }
  const data = image.data.data;
  for (let row = 0; row < strip.length; row++) {
    // Dots run from the near end at the bottom, image rows from the top.
    const at = (offset + strip[row]!.index) * 3, pixel = (strip.length - 1 - row) * 4;
    data[pixel] = toScreen[frame[at] ?? 0]!; data[pixel + 1] = toScreen[frame[at + 1] ?? 0]!;
    data[pixel + 2] = toScreen[frame[at + 2] ?? 0]!; data[pixel + 3] = 255;
  }
  image.context.putImageData(image.data, 0, 0);
  return image;
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
  context.fillStyle = palette.background; context.fillRect(0, 0, width, height);
  const { left, right, top, bottom, label } = boardTile;
  boards.forEach((board, position) => {
    const x = position * boardTile.width;
    // The board itself: a dark surface with a faint outline, which is all an unlit board shows.
    context.fillStyle = palette.surface; context.fillRect(x + left, top, right - left, bottom - top);
    context.strokeStyle = palette.outline; context.lineWidth = 2; context.setLineDash(board.dashed ? [6, 5] : []);
    context.strokeRect(x + left, top, right - left, bottom - top); context.setLineDash([]);
    if (board.pixels) {
      // Light is added to what is there, so an LED that is off leaves the faint outline in place.
      context.imageSmoothingEnabled = true;
      context.globalCompositeOperation = 'lighter';
      for (const side of [0, 1] as const) {
        const light = stripLight(side, board.strips[side], board.pixels, board.offset ?? 0);
        if (!light) continue;
        for (const pass of borderPasses) {
          context.globalAlpha = pass.alpha;
          context.drawImage(light.canvas, 0, 0, 1, light.data.height, x + (side === 0 ? left : right) - pass.width / 2, top, pass.width, bottom - top);
        }
      }
      context.globalAlpha = 1; context.globalCompositeOperation = 'source-over';
    }
    context.textAlign = 'center'; context.font = labelFont;
    context.fillStyle = palette.label; context.fillText(board.label, x + boardTile.width / 2, label, boardTile.width - 8);
    context.fillStyle = palette.note;
    if (board.note) context.fillText(board.note, x + boardTile.width / 2, (top + bottom) / 2, right - left - 24);
  });
}
/** Inline size for a canvas painted by `paintBoards`, so it keeps its shape while it scales down on a narrow screen. */
export function canvasShape(boards: number) {
  const count = Math.max(1, boards);
  return { aspectRatio: `${count * boardTile.width} / ${boardTile.height}`, maxWidth: `${count * boardTile.width}px` };
}
