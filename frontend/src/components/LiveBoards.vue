<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { boardDots, bySide, canvasShape, paintBoards } from '../lib/boardDrawing';
import { defaultStrips, useInstallationState } from '../lib/installation';
import { boardFrames, watchBoards } from '../lib/session';
import type { Board } from '../lib/contracts';

// Draws the frames BobFX is sending to the boards right now. A simulated controller shows them here only; a real
// one is mirrored here while its LEDs show the same. `boards` lets a page show its unsaved draft instead of the
// saved layout, so boards added or moved in the draft are shown before they are saved.
const props = defineProps<{ boards?: Board[]; activeId?: string }>();
const { settings, outputErrors } = useInstallationState();
const canvas = ref<HTMLCanvasElement>();
const row = computed(() => props.boards ?? settings.value?.boards ?? []);

// What stays the same from frame to frame. Frames arrive many times a second, so nothing here may read them.
const layout = computed(() => row.value.map((board, index) => {
  const device = settings.value?.devices.find(d => d.mac === board.deviceMac);
  // A simulated controller has a fixed wiring, the standard one, whatever the board's mapping says: that is what
  // makes a wrong mapping show up here as it would on real LEDs. A real controller's wiring is not known, so its
  // frames are drawn through the board's own mapping.
  const wiring = device?.simulated ? defaultStrips(device.ledCount) ?? board : board;
  return { board, label: `${index + 1}. ${board.name}`, strips: bySide(boardDots(wiring)),
    error: board.deviceMac ? outputErrors.value[board.deviceMac] : undefined, missing: !!board.deviceMac && !device,
    kind: !device ? '' : device.simulated ? 'Simulated' : device.name };
}));
// Which boards are receiving frames, as text: it only changes when a board goes lit or idle, and the status
// list is redrawn only then.
const lit = computed(() => layout.value.map(item => item.board.deviceMac && boardFrames.value[item.board.deviceMac] ? '1' : '0').join(''));
const items = computed(() => layout.value.map((item, index) => {
  const receiving = lit.value[index] === '1';
  return { ...item, lit: receiving, failed: !!item.error, problem: !!item.error || item.missing,
    state: !item.board.deviceMac ? 'No controller' : item.missing ? 'Controller not registered' : item.error ? item.error
      : receiving ? 'Receiving frames' : 'Idle' };
}));

// Painted at most once per screen refresh, however often frames arrive.
let scheduled = 0;
function paint() {
  scheduled = 0;
  if (!canvas.value) return;
  const frames = boardFrames.value;
  paintBoards(canvas.value, items.value.map(item => ({
    label: item.label, dashed: !item.board.deviceMac, strips: item.strips,
    pixels: item.board.deviceMac ? frames[item.board.deviceMac] ?? null : null,
    note: item.lit ? undefined : !item.board.deviceMac ? 'no controller' : 'idle',
  })));
}
function schedule() { scheduled ||= requestAnimationFrame(paint); }
watch([items, boardFrames], schedule, { flush: 'post' });
onMounted(() => { void watchBoards(true); schedule(); });
onBeforeUnmount(() => { cancelAnimationFrame(scheduled); void watchBoards(false); });
</script>

<template>
  <div class="live-boards">
    <p v-if="!items.length" class="preview-note">No boards yet.</p>
    <template v-else>
      <div class="preview-stage">
        <canvas ref="canvas" class="preview-canvas" role="img" data-testid="live-boards"
          :aria-label="`Live view of ${items.length} board${items.length === 1 ? '' : 's'}`" :style="canvasShape(items.length)"></canvas>
      </div>
      <!-- The dot says whether a board is lit; its state is spelled out only when something is wrong. -->
      <ul class="live-status" aria-label="Board status">
        <li v-for="item in items" :key="item.board.id" :class="{ lit: item.lit, failed: item.failed, active: item.board.id === activeId }" :title="item.state" data-testid="live-board-state">
          <span class="live-dot" aria-hidden="true"></span>
          <span><strong>{{ item.label }}</strong><template v-if="item.kind"> · {{ item.kind }}</template><span :class="{ 'sr-only': !item.problem }"> · {{ item.state }}</span></span>
        </li>
      </ul>
    </template>
  </div>
</template>
