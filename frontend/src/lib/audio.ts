// Audio.play must be called synchronously within the enable tap for iPad Safari.
export class BrowserAudio {
  enabled = false;
  private disposed = false;
  private readonly player: HTMLAudioElement;
  private readonly seen = new Set<string>();
  constructor(private readonly update: (enabled: boolean, status: string) => void) {
    this.player = new Audio('/sounds/game_over/game-over-yeah.mp3');
    this.player.preload = 'auto';
  }
  async toggle() {
    if (this.disposed) return;
    if (this.enabled) {
      this.player.pause(); this.enabled = false; this.update(false, 'Audio muted.'); return;
    }
    this.player.volume = 0;
    this.player.currentTime = 0;
    try {
      await this.player.play();
      if (this.disposed) return;
      this.player.pause(); this.player.currentTime = 0; this.player.volume = 1;
      this.enabled = true; this.update(true, 'Audio enabled in this browser.');
    } catch {
      if (!this.disposed) { this.player.volume = 1; this.enabled = false; this.update(false, 'Audio could not be enabled. Check sound settings and try again.'); }
    }
  }
  async play(event: { eventId: string; key: string }) {
    if (this.disposed || !this.enabled || event.key !== 'end' || this.seen.has(event.eventId)) return;
    this.seen.add(event.eventId);
    if (this.seen.size > 128) this.seen.delete(this.seen.values().next().value!);
    try { this.player.currentTime = 0; await this.player.play(); }
    catch { if (!this.disposed) { this.enabled = false; this.update(false, 'Sound playback failed. Enable audio again.'); } }
  }
  dispose() {
    this.disposed = true; this.enabled = false; this.player.pause();
    this.player.removeAttribute('src'); this.player.load();
  }
}
