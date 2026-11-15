import { Flag, Gamepad2, Timer } from '@lucide/vue';
import type { LightShowSegment } from './contracts';

/** The parts of the light show that can be previewed or tested by themselves, each with its icon. */
export const segments: Array<{ id: LightShowSegment; label: string; icon: typeof Timer }> = [
  { id: 'countdown', label: 'Countdown + GO', icon: Timer }, { id: 'playing', label: 'Playing', icon: Gamepad2 },
  { id: 'gameOver', label: 'Game over', icon: Flag }];
