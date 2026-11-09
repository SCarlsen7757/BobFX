<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useMutation, useQuery } from '@tanstack/vue-query';
import { api } from '../lib/api';
import { command, connected, preview, setPreview, useEffect } from '../lib/session';
import type { EffectDescriptor } from '../lib/contracts';
const { data: descriptors } = useQuery({ queryKey: ['effect-descriptors'], queryFn: () => api<EffectDescriptor[]>('/api/drgb/effects') });
const { data: status } = useEffect();
const selected = ref('Rainbow');
const descriptor = computed(() => descriptors.value?.find(effect => effect.type === selected.value));
const speed = ref(50), fade = ref(2), breath = ref(2), minimum = ref(0), maximum = ref(100);
const colors = ref<string[]>([]);
watch(descriptor, effect => {
  if (!effect) return;
  const [hours, minutes, seconds] = effect.info.defaultSpeed.split(':').map(Number);
  speed.value = Math.round(((hours ?? 0) * 3600 + (minutes ?? 0) * 60 + (seconds ?? 0)) * 1000);
  colors.value = Array.from({ length: effect.info.totalColors }, (_, i) => i === 0 ? '#ff0000' : selected.value === 'Strobe' ? '#00ff00' : '#0000ff');
  fade.value = 2; breath.value = 2; minimum.value = 0; maximum.value = 100;
}, { immediate: true });
onMounted(() => { void setPreview(true); });
onUnmounted(() => { void setPreview(false); });
const mutation = useMutation({ mutationFn: async (stop: boolean) => {
  if (stop) return command('/api/drgb/stop');
  if (!Number.isInteger(speed.value) || speed.value < 10 || speed.value > 2000) throw new Error('Speed must be 10–2000 ms.');
  await command('/api/drgb/start', {
    effect: selected.value, speedMs: speed.value, colors: colors.value,
    ...(selected.value === 'FadeIn' || selected.value === 'FadeOut' ? { fadeDurationSeconds: fade.value } : {}),
    ...(selected.value === 'Breathing' ? { breathDurationSeconds: breath.value, minBrightnessPercent: minimum.value, maxBrightnessPercent: maximum.value } : {}),
  });
} });
const busy = computed(() => !connected.value || mutation.isPending.value);
</script>
<template>
  <section class="settings-page effects-page">
    <h1 tabindex="-1">LED effects</h1>
    <div class="effect-summary"><span>Current effect: <strong>{{ status?.value.currentEffect ?? 'Off' }}</strong></span><span>Speed: <strong>{{ status?.value.speed ?? 100 }}</strong> ms</span></div>
    <div class="effect-picker"><label for="effect-type">Effect</label><select id="effect-type" v-model="selected"><option v-for="effect in descriptors" :key="effect.type" :value="effect.type">{{ effect.type }}</option></select></div>
    <p class="effect-description">{{ descriptor?.info.description }}</p>
    <div class="parameters effect-parameters">
      <div><label for="speed">Speed:</label><input id="speed" v-model.number="speed" type="number" inputmode="numeric" min="10" max="2000" /><span class="unit">ms</span></div>
      <p class="led-count">Preview: {{ status?.value.ledCount ?? 0 }} LEDs</p>
      <div v-for="(_, index) in colors" :key="`${selected}-${index}`"><label :for="`color-${index}`">{{ descriptor?.info.colorLabels[index] ?? `Color ${index + 1}` }} {{ index < (descriptor?.info.requiredColors ?? 0) ? '*' : '(optional)' }}:</label><input :id="`color-${index}`" v-model="colors[index]" type="color" /></div>
      <div v-if="selected === 'FadeIn' || selected === 'FadeOut'"><label for="fade">Fade Duration:</label><input id="fade" v-model.number="fade" type="number" min="0.1" max="60" step="0.1" /><span class="unit">seconds</span></div>
      <template v-if="selected === 'Breathing'">
        <div><label for="breath">Breath Duration:</label><input id="breath" v-model.number="breath" type="number" min="0.1" max="60" step="0.1" /><span class="unit">seconds</span></div>
        <div><label for="minimum">Min Brightness:</label><input id="minimum" v-model.number="minimum" type="number" min="0" max="100" /><span class="unit">%</span></div>
        <div><label for="maximum">Max Brightness:</label><input id="maximum" v-model.number="maximum" type="number" min="0" max="100" /><span class="unit">%</span></div>
      </template>
    </div>
    <div class="button-group"><button class="primary" :disabled="busy || !descriptor" @click="mutation.mutate(false)">Start</button><button class="danger" :disabled="busy" @click="mutation.mutate(true)">Stop</button></div>
    <p v-if="mutation.error.value" role="alert" class="error-message">{{ mutation.error.value.message }}</p>
    <div class="led-preview"><h2>LED Preview</h2><div class="led-strip" aria-label="Live LED preview"><span v-for="(pixel, index) in preview" :key="index" class="led-pixel" :style="{ background: pixel }"></span></div></div>
  </section>
</template>
