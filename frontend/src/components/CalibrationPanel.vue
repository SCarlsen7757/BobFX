<script setup lang="ts">
import { Check, ChevronLeft, ChevronRight, MoveUp, X } from '@lucide/vue';
import IconButton from './IconButton.vue';
import LiveBoards from './LiveBoards.vue';
import { checkColors } from '../lib/boardDrawing';
import type { Board } from '../lib/contracts';

// A check of the layout on the real boards: the board being checked, what a correct mapping looks like, and the
// way to the next board. `step` counts through the boards that have a controller.
defineProps<{ boards: Board[]; board: Board; position: number; step: number; steps: number; busy: boolean; canCancel: boolean }>();
defineEmits<{ next: [delta: number]; finish: []; cancel: [] }>();
</script>

<template>
  <section class="calibration-panel" aria-label="Check on the boards">
    <h2>Check {{ position + 1 }}. {{ board.name }}</h2>
    <!-- What a correctly mapped board shows, as a key instead of a paragraph. -->
    <p class="check-key" title="The left strip is blue, the right strip amber, and the white dots run away from you">
      <span><span class="strip-swatch" :style="{ background: checkColors.left }"></span>Left</span>
      <span><span class="strip-swatch" :style="{ background: checkColors.right }"></span>Right</span>
      <span role="img" aria-label="The white dots run away from you"><span class="strip-swatch check-dot"></span><MoveUp /></span>
    </p>
    <LiveBoards :boards="boards" :active-id="board.id" />
    <div class="button-group">
      <IconButton :icon="ChevronLeft" label="Previous board" :disabled="busy || step <= 0" @click="$emit('next', -1)" />
      <span class="check-step">{{ step + 1 }} / {{ steps }}</span>
      <IconButton :icon="ChevronRight" label="Next board" :disabled="busy || step === steps - 1" @click="$emit('next', 1)" />
      <button class="primary" :disabled="busy" aria-label="Finish and save" title="Finish and save" @click="$emit('finish')"><Check />Save</button>
      <IconButton :icon="X" label="Cancel check" :disabled="!canCancel" @click="$emit('cancel')" />
    </div>
  </section>
</template>
