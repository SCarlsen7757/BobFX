<script setup lang="ts">
import { stripColors } from '../lib/boardDrawing';
import { sideNames, sides, type BoardProblem, type Side } from '../lib/installation';
import type { Board, SavedDevice } from '../lib/contracts';

// The LED numbers of a board's two strips, for a mapping the buttons cannot make. It edits the page's draft board
// in place and says `changed` after each edit, so a running check can show it on the boards. The page shows it
// only for a board with both strips mapped.
const props = defineProps<{ board: Board; device?: SavedDevice; problems: BoardProblem[]; disabled: boolean }>();
defineEmits<{ changed: [] }>();
function fieldProblem(side: Side, part: 'start' | 'count') {
  return props.problems.find(problem => problem.side === side && problem.part === part)?.message;
}
</script>

<template>
  <section class="settings-section" aria-label="LED numbers">
    <h2>LED numbers · {{ board.name }}</h2>
    <p v-if="device" class="preview-note">{{ device.name }} · LEDs 0–{{ device.ledCount - 1 }}</p>
    <div class="strip-editors">
      <fieldset v-for="side in sides" :key="side" :disabled="disabled"><legend :style="{ color: stripColors[side] }">{{ sideNames[side] }} strip</legend>
        <label :for="`start-${side}`">First LED</label><input :id="`start-${side}`" v-model.number="board[side]!.start" type="number" inputmode="numeric" min="0" :aria-invalid="!!fieldProblem(side, 'start')" :aria-describedby="`start-${side}-problem`" @change="$emit('changed')" />
        <p v-if="fieldProblem(side, 'start')" :id="`start-${side}-problem`" class="field-problem" role="status">{{ fieldProblem(side, 'start') }}</p>
        <label :for="`count-${side}`">LED count</label><input :id="`count-${side}`" v-model.number="board[side]!.count" type="number" inputmode="numeric" min="1" :aria-invalid="!!fieldProblem(side, 'count')" :aria-describedby="`count-${side}-problem`" @change="$emit('changed')" />
        <p v-if="fieldProblem(side, 'count')" :id="`count-${side}-problem`" class="field-problem" role="status">{{ fieldProblem(side, 'count') }}</p>
        <label class="checkbox-label"><input v-model="board[side]!.reversed" type="checkbox" @change="$emit('changed')" />Reversed</label>
      </fieldset>
    </div>
    <!-- Problems that belong to both strips, such as two strips sharing LEDs. -->
    <p v-for="problem in problems.filter(p => !p.part && (p.side || p.bothStrips))" :key="problem.message" class="field-problem mapping-problem" role="status">{{ problem.message }}</p>
  </section>
</template>
