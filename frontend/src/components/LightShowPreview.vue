<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { Pause, Play } from '@lucide/vue';
import { api } from '../lib/api';
import IconButton from './IconButton.vue';
import { boardDots, canvasShape, paintBoards, type PaintedBoard } from '../lib/boardDrawing';
import { segments } from '../lib/segments';
import type { AnimationSettings, LightShowPreview, LightShowSegment, LightShowSettings } from '../lib/contracts';

// Plays server-rendered frames of the unsaved draft on drawings of the boards. The server uses the same renderer
// as the real boards, so this shows what WLED would receive; only the step from LED light to a screen is approximate.
const props = defineProps<{ draft: { lightShow: LightShowSettings; animation: AnimationSettings } }>();
const segment = ref<LightShowSegment>();
const preview = ref<LightShowPreview>();
const error = ref('');
const loading = ref(false);
const playing = ref(true);
const time = ref(0);
const canvas = ref<HTMLCanvasElement>();
let frames: Uint8Array[] = [];
// Every board of the row with its place in a frame; a frame holds all boards' LEDs one after another.
let layout: Array<Omit<PaintedBoard, 'pixels'>> = [];
let animation = 0, started = 0, request = 0, debounce: ReturnType<typeof setTimeout> | undefined;

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
      const dots = boardDots(board).filter(dot => dot.index < board.ledCount);
      const placed = { label: board.assigned ? `${index + 1}. ${board.name}` : `${index + 1}. ${board.name} · not assigned`, dashed: !board.assigned, dots, offset };
      offset += board.ledCount;
      return placed;
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
  const frame = frames[Math.max(0, Math.min(frames.length - 1, Math.floor(time.value * result.framesPerSecond)))]!;
  if (canvas.value) paintBoards(canvas.value, layout.map(board => ({ ...board, pixels: frame })));
  if (playing.value) animation = requestAnimationFrame(tick);
}
function toggle() { playing.value = !playing.value; if (playing.value) restart(); }
const moment = computed(() => {
  const t = time.value, timing = props.draft.animation;
  if (segment.value === 'countdown') return t < 3 ? `Countdown ${Math.ceil(3 - t)}` : t < 3.75 ? 'GO' : 'Into play';
  if (segment.value === 'playing') return 'Playing';
  return t < timing.flashSeconds ? 'Flares' : 'Comet';
});
onBeforeUnmount(() => { cancelAnimationFrame(animation); clearTimeout(debounce); request++; });
</script>

<template>
  <section class="settings-section light-preview" aria-label="Preview">
    <h2>Preview</h2>
    <div class="button-group">
      <button v-for="item in segments" :key="item.id" :aria-pressed="segment === item.id" :class="{ primary: segment === item.id }" @click="choose(item.id)"><component :is="item.icon" />{{ item.label }}</button>
      <IconButton v-if="preview" :icon="playing ? Pause : Play" :label="playing ? 'Pause' : 'Play'" :disabled="!segment" @click="toggle" />
    </div>
    <p v-if="error" class="error-message" role="alert">{{ error }}</p>
    <template v-if="preview && segment">
      <p class="preview-status" data-testid="preview-status">{{ moment }} · {{ time.toFixed(1) }} s of {{ preview.seconds.toFixed(1) }} s{{ loading ? ' · updating…' : '' }}</p>
      <div class="preview-stage"><canvas ref="canvas" class="preview-canvas" role="img" :aria-label="`Light show preview of ${preview.boards.length} board${preview.boards.length === 1 ? '' : 's'}`" :style="canvasShape(preview.boards.length)"></canvas></div>
    </template>
  </section>
</template>
