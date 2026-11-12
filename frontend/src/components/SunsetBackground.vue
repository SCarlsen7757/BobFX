<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue';
// Settings pages put forms straight on the background: there the scene holds still and dims so text stays readable.
const props = defineProps<{ still?: boolean }>();
const host = ref<HTMLDivElement>();
const animated = ref(false);
const reduced = ref(false);
let dispose: (() => void) | undefined;
let generation = 0;
let failed = false;
let media: MediaQueryList;
let mounting = false;

async function update() {
  const current = ++generation;
  dispose?.(); dispose = undefined; animated.value = false;
  if (!mounting || reduced.value || failed || props.still || !host.value) return;
  try {
    const { createSunset } = await import('../lib/sunset');
    if (current !== generation || !host.value) return;
    dispose = createSunset(host.value, () => {
      failed = true; animated.value = false;
      // Context loss and slow devices retain the already-visible static scene.
      setTimeout(() => { dispose?.(); dispose = undefined; }, 0);
    });
    animated.value = true;
  } catch { failed = true; }
}
function change() { reduced.value = media.matches; void update(); }
onMounted(() => {
  mounting = true; media = matchMedia('(prefers-reduced-motion: reduce)');
  reduced.value = media.matches; media.addEventListener('change', change); void update();
});
watch(() => props.still, () => { void update(); });
onUnmounted(() => { mounting = false; generation++; media?.removeEventListener('change', change); dispose?.(); });
</script>

<template>
  <div ref="host" class="sunset-background" :class="{ dimmed: still }" aria-hidden="true" :data-renderer="animated ? 'webgl' : 'static'">
    <div class="sunset-static"><div class="sunset-sun"></div><div class="sunset-horizon"></div><div class="sunset-mountains"></div></div>
  </div>
</template>
