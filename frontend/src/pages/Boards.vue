<script setup lang="ts">
import LightShowTestControls from '../components/LightShowTestControls.vue';
import LiveBoards from '../components/LiveBoards.vue';
import { useInstallationState } from '../lib/installation';
import { usePageStatus } from '../lib/status';

// The boards as they are lit right now. With simulated controllers this page is the boards: games, tests,
// calibration, Locate and manual effects all show here without any hardware.
const { settings } = useInstallationState();
const status = usePageStatus();
// Plays the saved light show; the Light show page tests unsaved settings.
function saved() {
  if (!settings.value) throw new Error('Reconnect before testing the light show.');
  return settings.value;
}
</script>

<template>
  <section class="settings-page boards-page">
    <h1 tabindex="-1">Live boards</h1>
    <p v-if="status.error.value" class="error-message" role="alert">{{ status.error.value }}</p>
    <LiveBoards />
    <section class="settings-section" aria-label="Try the light show">
      <LightShowTestControls :status="status" :show="saved" testid="boards-test-status" />
    </section>
  </section>
</template>
