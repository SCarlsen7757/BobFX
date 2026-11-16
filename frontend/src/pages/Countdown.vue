<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { Hourglass } from '@lucide/vue';
import SaveBar from '../components/SaveBar.vue';
import StatusMessage from '../components/StatusMessage.vue';
import { endpoints } from '../lib/endpoints';
import { isIntBetween } from '../lib/installation';
import { useUnsavedGuard } from '../lib/unsaved';
import { command, connected, defaults, useGame } from '../lib/session';
import { usePageStatus } from '../lib/status';

const { data } = useGame();
const game = computed(() => data.value?.value);
// The saved values; the form starts from them and only Save changes them.
const minutes = ref(12), seconds = ref(0), deviation = ref(120);
const duration = computed(() => minutes.value * 60 + seconds.value);
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
const status = usePageStatus();
const save = status.action(async () => {
  if (!isIntBetween(minutes.value, 0, 180) || !isIntBetween(seconds.value, 0, 59) || !isIntBetween(deviation.value, 0, 300) || duration.value <= deviation.value)
    throw new Error('Use 0–180 minutes, 0–59 seconds and 0–300 random seconds; the duration must exceed the random seconds.');
  await command(endpoints.installation.countdown, { durationSeconds: duration.value, deviationSeconds: deviation.value }, 'PUT');
  return 'Countdown saved.';
});
const changed = computed(() => !!defaults.value &&
  (duration.value !== defaults.value.durationSeconds || deviation.value !== defaults.value.deviationSeconds));
useUnsavedGuard(changed);
</script>
<template>
  <section class="settings-page">
    <h1 tabindex="-1">Countdown settings</h1>
    <!-- A timer is not announced as it ticks; a status would be read out every second of a game. -->
    <p class="remaining" role="timer" title="Remaining"><Hourglass /><span class="sr-only">Remaining: </span><strong>{{ remaining }}</strong></p>
    <div class="parameters countdown-parameters">
      <div><label for="minutes">Minutes</label><input id="minutes" v-model.number="minutes" type="number" inputmode="numeric" min="0" max="180" /></div>
      <div><label for="seconds">Seconds</label><input id="seconds" v-model.number="seconds" type="number" inputmode="numeric" min="0" max="59" /></div>
      <div><label for="deviation">Random ± seconds</label><input id="deviation" v-model.number="deviation" type="number" inputmode="numeric" min="0" max="300" /></div>
    </div>
    <StatusMessage :status="status" />
    <SaveBar :dirty="changed" label="Save" :disabled="!connected || status.busy.value || !changed" reload-label="Reload saved settings" :reload-disabled="!changed"
      @save="save" @reload="load" />
  </section>
</template>
