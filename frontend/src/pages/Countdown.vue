<script setup lang="ts">
import { computed, ref } from 'vue';
import { useMutation } from '@tanstack/vue-query';
import { command, connected, useGame, useInstallation } from '../lib/session';
const { data } = useGame();
const installation = useInstallation();
const game = computed(() => data.value?.value);
const minutes = ref(15), seconds = ref(0), deviation = ref(120);
const remaining = computed(() => {
  const value = game.value?.remainingSeconds ?? 0;
  return [Math.floor(value / 3600), Math.floor(value / 60) % 60, value % 60].map(n => String(n).padStart(2, '0')).join(':');
});
const mutation = useMutation({ mutationFn: async (mode: 'start' | 'stop') => {
  if (mode === 'stop') return command('/api/countdown/stop');
  if (!Number.isInteger(minutes.value) || minutes.value < 0 || minutes.value > 180 ||
    !Number.isInteger(seconds.value) || seconds.value < 0 || seconds.value > 59 ||
    !Number.isInteger(deviation.value) || deviation.value < 0 || deviation.value > 300 ||
    minutes.value * 60 + seconds.value <= deviation.value)
    throw new Error('Enter valid limits. Total duration must exceed random deviation.');
  await command(`/api/countdown/${mode}`, { durationSeconds: minutes.value * 60 + seconds.value, deviationSeconds: deviation.value });
} });
const busy = computed(() => !connected.value || mutation.isPending.value || !!installation.data.value?.value.calibration);
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
      <button class="primary" :disabled="busy || game?.phase !== 'Idle'" @click="mutation.mutate('start')">Start</button>
      <button class="danger" :disabled="busy || game?.phase === 'Idle'" @click="mutation.mutate('stop')">Stop</button>
    </div>
    <p v-if="mutation.error.value" role="alert" class="error-message">{{ mutation.error.value.message }}</p>
  </section>
</template>
