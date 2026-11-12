<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useMutation } from '@tanstack/vue-query';
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
    throw new Error('Enter valid limits. Total duration must exceed random deviation.');
  await command('/api/installation/countdown', { durationSeconds: minutes.value * 60 + seconds.value, deviationSeconds: deviation.value }, 'PUT');
  message.value = 'Countdown saved.';
} });
const changed = computed(() => !!defaults.value &&
  (minutes.value * 60 + seconds.value !== defaults.value.durationSeconds || deviation.value !== defaults.value.deviationSeconds));
</script>
<template>
  <section class="settings-page">
    <h1 tabindex="-1">Countdown settings</h1>
    <p class="remaining" role="status">Remaining: <strong>{{ remaining }}</strong></p>
    <div class="parameters countdown-parameters">
      <div><label for="minutes">Minutes (0–180)</label><input id="minutes" v-model.number="minutes" type="number" inputmode="numeric" min="0" max="180" /></div>
      <div><label for="seconds">Seconds (0–59)</label><input id="seconds" v-model.number="seconds" type="number" inputmode="numeric" min="0" max="59" /></div>
      <div><label for="deviation">Random deviation seconds (0–300)</label><input id="deviation" v-model.number="deviation" type="number" inputmode="numeric" min="0" max="300" /></div>
    </div>
    <div class="button-group">
      <button class="primary" :disabled="!connected || mutation.isPending.value || !changed" @click="mutation.mutate()">Save</button>
    </div>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
  </section>
</template>
