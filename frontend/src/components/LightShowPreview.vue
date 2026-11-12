<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { api } from '../lib/api';
import { boardPixels } from '../lib/installation';
import type { AnimationSettings, LightShowPreview, LightShowSegment, LightShowSettings } from '../lib/contracts';

// Plays server-rendered frames of the unsaved draft on drawings of the boards. The server uses the same renderer
// as the real boards, so this shows what WLED would receive; only the step from LED light to a screen is approximate.
const props = defineProps<{ draft: { lightShow: LightShowSettings; animation: AnimationSettings } }>();
const segments: Array<{ id: LightShowSegment; label: string }> = [
  { id: 'countdown', label: 'Countdown + GO' }, { id: 'playing', label: 'Playing' }, { id: 'gameOver', label: 'Game over' }];
const segment = ref<LightShowSegment>();
const preview = ref<LightShowPreview>();
const error = ref('');
const loading = ref(false);
const playing = ref(true);
const time = ref(0);
const canvas = ref<HTMLCanvasElement>();
let frames: Uint8Array[] = [];
let layout: Array<{ left: number; label: string; assigned: boolean; dots: Array<{ x: number; y: number; at: number }> }> = [];
let animation = 0, started = 0, request = 0, debounce: ReturnType<typeof setTimeout> | undefined;

// LEDs emit linear light; a screen expects gamma-encoded values, so frames are encoded back before drawing.
const toScreen = Uint8Array.from({ length: 256 }, (_, v) => Math.round(255 * Math.pow(v / 255, 1 / 2.2)));
// Each board is a 220 px tile with a strip underneath for its name.
const Tile = 220, Inset = 10, Height = 248;

async function load() {
  if (!segment.value) return;
  const current = ++request;
  loading.value = true;
  try {
    const result = await api<LightShowPreview>('/api/installation/light-show/preview', 'POST', { segment: segment.value, ...props.draft });
    if (current !== request) return;
    preview.value = result; error.value = '';
    frames = result.frames.map(frame => Uint8Array.from(atob(frame), c => c.charCodeAt(0)));
    let offset = 0;
    layout = result.boards.map((board, index) => {
      const dots = boardPixels({ id: board.id, name: board.name, deviceMac: '', rotation: board.rotation, edges: board.edges })
        .filter(pixel => pixel.index < board.ledCount)
        .map(pixel => ({ x: index * Tile + Inset + pixel.x, y: Inset + pixel.y, at: (offset + pixel.index) * 3 }));
      offset += board.ledCount;
      return { left: index * Tile, label: `${index + 1}. ${board.name}`, assigned: board.assigned, dots };
    });
    await nextTick();
    restart();
  } catch (failure) {
    if (current === request) error.value = (failure as Error).message;
  } finally { if (current === request) loading.value = false; }
}
function choose(id: LightShowSegment) { segment.value = id; playing.value = true; void load(); }
// Changing a setting re-renders the segment on show, so the preview follows the form.
watch(() => props.draft, () => {
  if (!segment.value) return;
  clearTimeout(debounce); debounce = setTimeout(() => { void load(); }, 400);
}, { deep: true });

// Draws the current frame at once, so the canvas already shows freshly loaded frames when "updating…" clears.
function restart() { const now = performance.now(); started = now - time.value * 1000; cancelAnimationFrame(animation); tick(now); }
function tick(now: number) {
  const result = preview.value;
  if (!result || !frames.length) return;
  // A frame's timestamp can be a moment before the start recorded in restart(); never look up a frame before the first.
  if (playing.value) time.value = (Math.max(0, now - started) / 1000) % (frames.length / result.framesPerSecond);
  draw(frames[Math.max(0, Math.min(frames.length - 1, Math.floor(time.value * result.framesPerSecond)))]!);
  if (playing.value) animation = requestAnimationFrame(tick);
}
function draw(frame: Uint8Array) {
  const element = canvas.value;
  if (!element || !preview.value) return;
  const scale = window.devicePixelRatio || 1, width = preview.value.boards.length * Tile, height = Height;
  if (element.width !== width * scale) { element.width = width * scale; element.height = height * scale; }
  const context = element.getContext('2d');
  if (!context) return;
  context.setTransform(scale, 0, 0, scale, 0, 0);
  context.fillStyle = '#0b0614'; context.fillRect(0, 0, width, height);
  for (const board of layout) {
    // An unassigned board is drawn with a dashed outline, as on Board layout.
    context.strokeStyle = '#3a2648'; context.lineWidth = 2; context.setLineDash(board.assigned ? [] : [6, 5]);
    context.strokeRect(board.left + Inset + 10, Inset + 10, 180, 180); context.setLineDash([]);
    context.textAlign = 'center'; context.font = '12px "Space Grotesk Variable", sans-serif';
    context.fillStyle = '#c0adcd'; context.fillText(board.assigned ? board.label : `${board.label} · not assigned`, board.left + Tile / 2, Height - 12);
    for (const dot of board.dots) {
      const r = toScreen[frame[dot.at]!]!, g = toScreen[frame[dot.at + 1]!]!, b = toScreen[frame[dot.at + 2]!]!;
      if (r + g + b === 0) { context.fillStyle = '#1c1228'; context.beginPath(); context.arc(dot.x, dot.y, 3, 0, Math.PI * 2); context.fill(); continue; }
      // A soft halo stands in for the light an LED throws around itself.
      context.shadowColor = `rgb(${r} ${g} ${b})`; context.shadowBlur = 10;
      context.fillStyle = `rgb(${r} ${g} ${b})`; context.beginPath(); context.arc(dot.x, dot.y, 4, 0, Math.PI * 2); context.fill();
      context.shadowBlur = 0;
    }
  }
}
function toggle() { playing.value = !playing.value; if (playing.value) restart(); }
const moment = computed(() => {
  const t = time.value, timing = props.draft.animation;
  if (segment.value === 'countdown') return t < 3 ? `Countdown ${Math.ceil(3 - t)}` : t < 3.75 ? 'GO' : 'Into play';
  if (segment.value === 'playing') return 'Playing';
  return t < timing.flashSeconds ? 'Flares' : 'Comet';
});
const note = computed(() => {
  const value = preview.value;
  if (!value) return '';
  if (value.exampleBoards) return 'Board layout has no boards yet, so this uses two example boards with 60 LEDs each.';
  const missing = value.boards.filter(board => !board.assigned).length;
  if (!missing) return 'Boards in row order, as in Board layout, with the near side at the bottom.';
  // Boards without a controller copy the first board that has one; the server falls back to 60 LEDs only when none has.
  const model = value.boards.find(board => board.assigned);
  const looks = model ? `the same ${model.ledCount} LEDs and mapping as ${model.name}` : '60 LEDs each';
  const one = missing === 1;
  return `Boards in row order, as in Board layout, with the near side at the bottom. ${one ? 'The board' : `The ${missing} boards`} marked “not assigned” ${one ? 'is' : 'are'} drawn with ${looks}; a game skips ${one ? 'it' : 'them'} until ${one ? 'it has' : 'they have'} a controller.`;
});
onBeforeUnmount(() => { cancelAnimationFrame(animation); clearTimeout(debounce); request++; });
</script>

<template>
  <section class="settings-section light-preview" aria-label="Preview">
    <h2>Preview</h2>
    <p>Plays the show with the settings above on drawings of your boards. Screens cannot match LED brightness, so judge colors and movement here and brightness on the boards.</p>
    <div class="button-group">
      <button v-for="item in segments" :key="item.id" :aria-pressed="segment === item.id" :class="{ primary: segment === item.id }" @click="choose(item.id)">{{ item.label }}</button>
      <button v-if="preview" :disabled="!segment" @click="toggle">{{ playing ? 'Pause' : 'Play' }}</button>
    </div>
    <p v-if="error" class="error-message" role="alert">{{ error }}</p>
    <template v-if="preview && segment">
      <p class="preview-status" data-testid="preview-status">{{ moment }} · {{ time.toFixed(1) }} s of {{ preview.seconds.toFixed(1) }} s{{ loading ? ' · updating…' : '' }}</p>
      <div class="preview-stage"><canvas ref="canvas" class="preview-canvas" role="img" :aria-label="`Light show preview of ${preview.boards.length} board${preview.boards.length === 1 ? '' : 's'}`" :style="{ aspectRatio: `${preview.boards.length * Tile} / ${Height}`, maxWidth: `${preview.boards.length * Tile}px` }"></canvas></div>
      <p class="preview-note">{{ note }}</p>
    </template>
  </section>
</template>
