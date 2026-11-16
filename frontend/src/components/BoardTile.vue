<script setup lang="ts">
import { computed } from 'vue';
import { boardDots, boardTile, checkColors, stripColors } from '../lib/boardDrawing';
import { sideNames, sides, stripRange, unmappedLeds, type Side } from '../lib/installation';
import type { Board, SavedDevice } from '../lib/contracts';

// One board of the layout row, drawn with every LED of its two strips. The whole drawing is the button that
// selects the board; `checking` shows it in the colors a correctly mapped board has during a check.
const props = defineProps<{ board: Board; position: number; device?: SavedDevice; selected: boolean; checking: boolean }>();
const dots = computed(() => boardDots(props.board).map(dot => ({ ...dot, strip: (dot.side === 0 ? 'left' : 'right') as Side })));
const colors = computed(() => props.checking ? checkColors : stripColors);
const ranges = computed(() => sides.map(side => ({ side, text: `${sideNames[side]} ${stripRange(props.board[side]).replace('LEDs ', '').replace('LED ', '')}` })));
/** The board's place in the row and its name, cut to what fits between the two strips of its drawing. */
const name = computed(() => {
  const label = `${props.position + 1}. ${props.board.name}`;
  return label.length > 13 ? `${label.slice(0, 12)}…` : label;
});
const spare = computed(() => {
  const unused = props.device && props.board.left && props.board.right ? unmappedLeds(props.board, props.device.ledCount) : 0;
  return unused === 0 ? '' : unused === 1 ? '1 LED unused' : `${unused} LEDs unused`;
});
</script>

<template>
  <button class="board-tile" :class="{ selected }" :aria-pressed="selected">
    <svg :viewBox="`0 0 ${boardTile.width} ${boardTile.bottom + 12}`" role="img" :aria-label="`${board.name}: ${ranges.map(range => range.text).join(', ')}`">
      <rect :x="boardTile.left" :y="boardTile.top" :width="boardTile.right - boardTile.left" :height="boardTile.bottom - boardTile.top" rx="3" class="board-surface" stroke-width="3" />
      <template v-for="dot in dots" :key="`${dot.side}-${dot.index}`">
        <circle v-if="dot.first" class="strip-start" :cx="dot.x" :cy="dot.y" r="6" fill="none" :stroke="colors[dot.strip]" stroke-width="1.5" />
        <circle :cx="dot.x" :cy="dot.y" r="2" :fill="colors[dot.strip]"><title>{{ sideNames[dot.strip] }} strip · LED {{ dot.index }}{{ dot.first ? ' (first LED)' : '' }}</title></circle>
      </template>
      <text class="board-name" :x="boardTile.width / 2" :y="boardTile.nameY" text-anchor="middle">{{ name }}</text>
      <template v-if="board.left || board.right">
        <!-- The strip's own color on its range stands in for a legend. -->
        <text v-for="(range, row) in ranges" :key="range.side" :x="boardTile.width / 2" :y="boardTile.rangeY[row]" text-anchor="middle" :style="{ fill: colors[range.side] }">{{ range.text }}</text>
        <text v-if="spare" :x="boardTile.width / 2" :y="boardTile.spareY" text-anchor="middle">{{ spare }}</text>
      </template>
    </svg>
  </button>
</template>
