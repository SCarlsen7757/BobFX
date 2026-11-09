import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BrowserAudio } from '../lib/audio';

class FakeAudio {
  static instances: FakeAudio[] = [];
  plays = 0; volume = 1; currentTime = 0; failure = false; released = false;
  constructor(public path: string) { FakeAudio.instances.push(this); }
  play() { this.plays++; return this.failure ? Promise.reject(new Error('Blocked')) : Promise.resolve(); }
  pause() {}
  removeAttribute() { this.path = ''; }
  load() { this.released = true; }
}
beforeEach(() => { FakeAudio.instances = []; vi.stubGlobal('Audio', FakeAudio); });
afterEach(() => vi.unstubAllGlobals());
describe('browser audio', () => {
  it('starts audio synchronously inside the enable gesture', async () => {
    const audio = new BrowserAudio(vi.fn());
    const pending = audio.toggle();
    expect(FakeAudio.instances[0]!.plays).toBe(1);
    await pending; expect(audio.enabled).toBe(true); audio.dispose();
  });
  it('keeps browser sessions independent and deduplicates end events', async () => {
    const first = new BrowserAudio(vi.fn()), second = new BrowserAudio(vi.fn());
    await first.toggle();
    await first.play({ eventId: 'one', key: 'end' });
    await first.play({ eventId: 'one', key: 'end' });
    await second.play({ eventId: 'one', key: 'end' });
    expect(FakeAudio.instances[0]!.plays).toBe(2); expect(FakeAudio.instances[1]!.plays).toBe(0);
    first.dispose(); second.dispose();
  });
  it('recovers from blocked playback and releases the media element', async () => {
    const update = vi.fn(); const audio = new BrowserAudio(update); const player = FakeAudio.instances[0]!;
    player.failure = true; await audio.toggle(); expect(audio.enabled).toBe(false);
    player.failure = false; await audio.toggle(); expect(audio.enabled).toBe(true);
    audio.dispose(); expect(player.released).toBe(true); expect(player.path).toBe('');
  });
  it('does not re-enable an audio session disposed during initialization', async () => {
    const audio = new BrowserAudio(vi.fn());
    const pending = audio.toggle(); audio.dispose(); await pending; expect(audio.enabled).toBe(false);
  });
});
