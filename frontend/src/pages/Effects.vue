<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import { Play, Square, Zap } from '@lucide/vue';
import { api } from '../lib/api';
import { endpoints } from '../lib/endpoints';
import { inRange, rangeText, useLightsOwner } from '../lib/installation';
import { command, connected, limits, preview, setPreview, requireLimits, useEffect } from '../lib/session';
import { usePageStatus } from '../lib/status';
import type { EffectDescriptor } from '../lib/contracts';

const { data: descriptors } = useQuery({ queryKey: ['effect-descriptors'], queryFn: () => api<EffectDescriptor[]>(endpoints.effects.list), staleTime: Infinity });
const { data: current } = useEffect();
const owner = useLightsOwner();
const selected = ref('Rainbow');
const descriptor = computed(() => descriptors.value?.find(effect => effect.type === selected.value));
// The parameters an effect takes are listed by the server; these are the fields this page has for them.
const parameters = computed(() => Object.keys(descriptor.value?.info.customParameters ?? {}));
const hasFade = computed(() => parameters.value.includes('FadeDuration'));
const hasBreath = computed(() => parameters.value.includes('BreathDuration'));
const speed = ref(50), fade = ref(2), breath = ref(2), minimum = ref(0), maximum = ref(100);
const colors = ref<string[]>([]);
watch(descriptor, effect => {
  if (!effect) return;
  speed.value = Math.round(effect.info.defaultSpeedMs);
  // Red first. A strobe's second color is its flash, shown green so the two are told apart at a glance.
  colors.value = Array.from({ length: effect.info.totalColors }, (_, i) => i === 0 ? '#ff0000' : selected.value === 'Strobe' ? '#00ff00' : '#0000ff');
  fade.value = 2; breath.value = 2; minimum.value = 0; maximum.value = 100;
}, { immediate: true });
onMounted(() => { void setPreview(true); });
onUnmounted(() => { void setPreview(false); });
const status = usePageStatus();
const start = status.action(async () => {
  const allowed = requireLimits().effectSpeedMs;
  if (!inRange(speed.value, allowed, true)) throw new Error(`Speed must be ${rangeText(allowed)} ms.`);
  await command(endpoints.effects.start, {
    effect: selected.value, speedMs: speed.value, colors: colors.value,
    ...(hasFade.value ? { fadeDurationSeconds: fade.value } : {}),
    ...(hasBreath.value ? { breathDurationSeconds: breath.value, minBrightnessPercent: minimum.value, maxBrightnessPercent: maximum.value } : {}),
  });
});
const stop = status.action(async () => { await command(endpoints.effects.stop); });
// A game is not listed: starting an effect during one is refused by the server, and the page says why.
const busy = computed(() => !connected.value || status.busy.value || owner.value === 'calibration' || owner.value === 'lightTest' || owner.value === 'locating');
</script>
<template>
  <section class="settings-page effects-page">
    <h1 tabindex="-1">LED effects</h1>
    <p class="effect-summary" title="Current effect and speed"><Zap /><span class="sr-only">Current effect: </span><strong>{{ current?.value.currentEffect ?? 'Off' }}</strong><span>{{ current?.value.speed ?? 100 }} ms</span></p>
    <div class="effect-picker"><label for="effect-type">Effect</label><select id="effect-type" v-model="selected" :title="descriptor?.info.description"><option v-for="effect in descriptors" :key="effect.type" :value="effect.type">{{ effect.type }}</option></select></div>
    <div class="parameters effect-parameters">
      <div><label for="speed">Speed:</label><input id="speed" v-model.number="speed" type="number" inputmode="numeric" :min="limits?.effectSpeedMs.min" :max="limits?.effectSpeedMs.max" /><span class="unit">ms</span></div>
      <div v-for="(_, index) in colors" :key="`${selected}-${index}`"><label :for="`color-${index}`">{{ descriptor?.info.colorLabels[index] ?? `Color ${index + 1}` }}:</label><input :id="`color-${index}`" v-model="colors[index]" type="color" /></div>
      <div v-if="hasFade"><label for="fade">Fade Duration:</label><input id="fade" v-model.number="fade" type="number" min="0.1" max="60" step="0.1" /><span class="unit">s</span></div>
      <template v-if="hasBreath">
        <div><label for="breath">Breath Duration:</label><input id="breath" v-model.number="breath" type="number" min="0.1" max="60" step="0.1" /><span class="unit">s</span></div>
        <div><label for="minimum">Min Brightness:</label><input id="minimum" v-model.number="minimum" type="number" min="0" max="100" /><span class="unit">%</span></div>
        <div><label for="maximum">Max Brightness:</label><input id="maximum" v-model.number="maximum" type="number" min="0" max="100" /><span class="unit">%</span></div>
      </template>
    </div>
    <div class="button-group"><button class="primary" :disabled="busy || !descriptor" @click="start"><Play />Start</button><button class="danger" :disabled="busy" @click="stop"><Square />Stop</button></div>
    <p v-if="status.error.value" role="alert" class="error-message">{{ status.error.value }}</p>
    <div class="led-preview"><div class="led-strip" role="img" aria-label="Live LED preview"><span v-for="(pixel, index) in preview" :key="index" class="led-pixel" :style="{ background: pixel }"></span></div></div>
  </section>
</template>
