<script setup lang="ts">
import { computed } from 'vue';
import { useMutation } from '@tanstack/vue-query';
import { Square } from '@lucide/vue';
import IconButton from '../components/IconButton.vue';
import LiveBoards from '../components/LiveBoards.vue';
import { command, connected, useInstallation } from '../lib/session';
import { useCanConfigure } from '../lib/installation';
import { segments } from '../lib/segments';
import type { LightShowSegment } from '../lib/contracts';

// The boards as they are lit right now. With simulated controllers this page is the boards: games, tests,
// calibration, Locate and manual effects all show here without any hardware.
const installation = useInstallation();
const lightsFree = useCanConfigure(true);
const settings = computed(() => installation.data.value?.value.settings);
const test = computed(() => installation.data.value?.value.lightShowTest ?? null);
const hasBoards = computed(() => !!settings.value?.boards.some(board => board.deviceMac));
const mutation = useMutation({ mutationFn: async (action: LightShowSegment | 'stop') => {
  if (action === 'stop') { await command('/api/installation/light-show/test', undefined, 'DELETE'); return; }
  // Plays the saved light show; the Light show page tests unsaved settings.
  await command('/api/installation/light-show/test', { segment: action, lightShow: settings.value!.lightShow, animation: settings.value!.animation });
} });
const busy = computed(() => !connected.value || mutation.isPending.value);
const status = computed(() => test.value
  ? `Playing “${segments.find(segment => segment.id === test.value?.segment)?.label}”`
  : !hasBoards.value ? 'No board has a controller.'
  : !lightsFree.value && connected.value ? 'The lights are in use.' : '');
</script>

<template>
  <section class="settings-page boards-page">
    <h1 tabindex="-1">Live boards</h1>
    <p v-if="mutation.error.value" class="error-message" role="alert">{{ mutation.error.value.message }}</p>
    <LiveBoards />
    <section class="settings-section" aria-label="Try the light show">
      <div class="button-group">
        <button v-for="segment in segments" :key="segment.id" :disabled="busy || !lightsFree || !hasBoards" @click="mutation.mutate(segment.id)"><component :is="segment.icon" />{{ segment.label }}</button>
        <IconButton class="danger" :icon="Square" label="Stop test" :disabled="busy || !test" @click="mutation.mutate('stop')" />
      </div>
      <p v-if="status" class="test-status" role="status" data-testid="boards-test-status">{{ status }}</p>
    </section>
  </section>
</template>
