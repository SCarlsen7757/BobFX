<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useMutation } from '@tanstack/vue-query';
import { Hourglass } from '@lucide/vue';
import SaveBar from '../components/SaveBar.vue';
import { useUnsavedGuard } from '../lib/unsaved';
import { command, connected, defaults, useGame } from '../lib/session';
const { data } = useGame();
const game = computed(() => data.value?.value);
// The saved values; the form starts from them and only Save changes them.
const minutes = ref(12), seconds = ref(0), deviation = ref(120);
const message = ref('');
function load() {
  const saved = defaults.value;
  if (!saved) return;
  minutes.value = Math.floor(saved.durationSeconds / 60);
  seconds.value = saved.durationSeconds % 60;
  deviation.value = saved.deviationSeconds;
}
let loaded = false;
watch(defaults, value => { if (value && !loaded) { loaded = true; load(); } }, { immediate: true });
const remaining = computed(() => {
  const value = game.value?.remainingSeconds ?? 0;
  return [Math.floor(value / 3600), Math.floor(value / 60) % 60, value % 60].map(n => String(n).padStart(2, '0')).join(':');
});
const mutation = useMutation({ mutationFn: async () => {
  message.value = '';
  if (!Number.isInteger(minutes.value) || minutes.value < 0 || minutes.value > 180 ||
    !Number.isInteger(seconds.value) || seconds.value < 0 || seconds.value > 59 ||
    !Number.isInteger(deviation.value) || deviation.value < 0 || deviation.value > 300 ||
    minutes.value * 60 + seconds.value <= deviation.value)
    throw new Error('Use 0–180 minutes, 0–59 seconds and 0–300 random seconds; the duration must exceed the random seconds.');
  await command('/api/installation/countdown', { durationSeconds: minutes.value * 60 + seconds.value, deviationSeconds: deviation.value }, 'PUT');
  message.value = 'Countdown saved.';
} });
const changed = computed(() => !!defaults.value &&
  (minutes.value * 60 + seconds.value !== defaults.value.durationSeconds || deviation.value !== defaults.value.deviationSeconds));
useUnsavedGuard(changed);
</script>
<template>
  <section class="settings-page">
    <h1 tabindex="-1">Countdown settings</h1>
    <p class="remaining" role="status" title="Remaining"><Hourglass /><span class="sr-only">Remaining: </span><strong>{{ remaining }}</strong></p>
    <div class="parameters countdown-parameters">
      <div><label for="minutes">Minutes</label><input id="minutes" v-model.number="minutes" type="number" inputmode="numeric" min="0" max="180" /></div>
      <div><label for="seconds">Seconds</label><input id="seconds" v-model.number="seconds" type="number" inputmode="numeric" min="0" max="59" /></div>
      <div><label for="deviation">Random ± seconds</label><input id="deviation" v-model.number="deviation" type="number" inputmode="numeric" min="0" max="300" /></div>
    </div>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
    <SaveBar :dirty="changed" label="Save" :disabled="!connected || mutation.isPending.value || !changed" reload-label="Reload saved settings" :reload-disabled="!changed"
      @save="mutation.mutate()" @reload="load" />
  </section>
</template>
