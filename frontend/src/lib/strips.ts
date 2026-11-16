import type { Strip } from './contracts';

/** WLED addresses LEDs with 16 bits, so no controller has more. */
export const maxLeds = 65535;

/** Whether a strip's numbers are usable at all, before they are checked against a controller. */
export function wellFormed(strip: Strip) {
  return Number.isInteger(strip.start) && Number.isInteger(strip.count) && strip.start >= 0 && strip.count >= 1 && strip.count <= maxLeds;
}
