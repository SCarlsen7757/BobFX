<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { boardDots, canvasShape, paintBoards } from '../lib/boardDrawing';
import { defaultStrips } from '../lib/installation';
import { boardFrames, useInstallation, watchBoards } from '../lib/session';
import type { Board } from '../lib/contracts';

// Draws the frames BobFX is sending to the boards right now. A simulated controller shows them here only; a real
// one is mirrored here while its LEDs show the same. `boards` lets a page show its unsaved draft instead of the
// saved layout, so boards added or moved in the draft are shown before they are saved.
const props = defineProps<{ boards?: Board[]; activeId?: string }>();
const installation = useInstallation();
const canvas = ref<HTMLCanvasElement>();
const saved = computed(() => installation.data.value?.value.settings);
const row = computed(() => props.boards ?? saved.value?.boards ?? []);
const errors = computed(() => installation.data.value?.value.outputErrors ?? {});

const boards = computed(() => row.value.map((board, index) => {
  const device = saved.value?.devices.find(d => d.mac === board.deviceMac);
  const pixels = board.deviceMac ? boardFrames.value[board.deviceMac] ?? null : null;
  const error = board.deviceMac ? errors.value[board.deviceMac] : undefined;
  const state = !board.deviceMac ? 'No controller' : !device ? 'Controller not registered' : error ? error
    : pixels ? 'Receiving frames' : 'Idle';
  // A simulated controller has a fixed wiring, the standard one, whatever the board's mapping says: that is what
  // makes a wrong mapping show up here as it would on real LEDs. A real controller's wiring is not known, so its
  // frames are drawn through the board's own mapping. The dots are worked out here so a mapping change redraws.
  const wiring = device?.simulated ? defaultStrips(device.ledCount) ?? board : board;
  return { board, device, pixels, state, lit: !!pixels, failed: !!error, problem: !!error || (!!board.deviceMac && !device), label: `${index + 1}. ${board.name}`, dots: boardDots(wiring),
    kind: !device ? '' : device.simulated ? 'Simulated' : device.name };
}));
function draw() {
  if (!canvas.value) return;
  paintBoards(canvas.value, boards.value.map(item => ({
    label: item.label, dashed: !item.board.deviceMac, dots: item.dots, pixels: item.pixels,
    note: item.lit ? undefined : !item.board.deviceMac ? 'no controller' : 'idle',
  })));
}
watch(boards, async () => { await nextTick(); draw(); }, { immediate: true });
onMounted(() => { void watchBoards(true); draw(); });
onBeforeUnmount(() => { void watchBoards(false); });
</script>

<template>
  <div class="live-boards">
    <p v-if="!boards.length" class="preview-note">No boards yet.</p>
    <template v-else>
      <div class="preview-stage">
        <canvas ref="canvas" class="preview-canvas" role="img" data-testid="live-boards"
          :aria-label="`Live view of ${boards.length} board${boards.length === 1 ? '' : 's'}`" :style="canvasShape(boards.length)"></canvas>
      </div>
      <!-- The dot says whether a board is lit; its state is spelled out only when something is wrong. -->
      <ul class="live-status" aria-label="Board status">
        <li v-for="item in boards" :key="item.board.id" :class="{ lit: item.lit, failed: item.failed, active: item.board.id === activeId }" :title="item.state" data-testid="live-board-state">
          <span class="live-dot" aria-hidden="true"></span>
          <span><strong>{{ item.label }}</strong><template v-if="item.kind"> · {{ item.kind }}</template><span :class="{ 'sr-only': !item.problem }"> · {{ item.state }}</span></span>
        </li>
      </ul>
    </template>
  </div>
</template>
