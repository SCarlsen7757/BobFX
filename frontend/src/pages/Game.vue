<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';
import { useMutation } from '@tanstack/vue-query';
import { command, connected, suppressedRun, useGame, useInstallation } from '../lib/session';
const route = useRoute();
const fullscreen = computed(() => !!route.meta.fullscreen);
const { data } = useGame();
const game = computed(() => data.value?.value);
const installation = useInstallation();
const locating = computed(() => !!installation.data.value?.value.locating);
const calibrating = computed(() => !!installation.data.value?.value.calibration);
const active = computed(() => !!(game.value?.running || game.value?.preCountdownRunning));
const gameOver = computed(() => game.value?.gameOver && suppressedRun.value !== `${data.value?.serverInstanceId}:${game.value.runId}`);
const title = computed(() => calibrating.value ? 'Calibrating boards' : locating.value ? 'Locating board' : game.value?.preCountdownRunning ? 'Get ready' : game.value?.running ? (game.value.opening ? 'BOB!' : 'Game on!') : gameOver.value ? 'Game Over!' : 'Ready to play');
const mutation = useMutation({ mutationFn: async (stop: boolean) => {
  const run = `${data.value?.serverInstanceId}:${game.value?.runId}`;
  await command(`/api/countdown/${stop ? 'stop' : 'start'}`);
  if (stop) suppressedRun.value = run;
} });
function action() {
  if (!active.value || confirm('Stop the countdown?')) mutation.mutate(active.value);
}
function displayMode() {
  if (fullscreen.value) {
    if (document.fullscreenElement) void document.exitFullscreen?.().catch(() => {});
  } else if (!document.fullscreenElement) void document.documentElement.requestFullscreen?.().catch(() => {});
}
</script>

<template>
  <div class="game-page">
    <header class="page-heading game-heading">
      <h1 tabindex="-1">Game</h1>
      <div class="game-heading-actions">
      <slot name="header-actions" />
      <RouterLink class="control-link" :to="fullscreen ? '/' : '/game/fullscreen'" @click="displayMode">
        <svg aria-hidden="true" viewBox="0 0 24 24"><path v-if="!fullscreen" d="M9 4H4v5m11-5h5v5M4 15v5h5m11-5v5h-5" /><path v-else d="M4 9h5V4m11 5h-5V4M4 15h5v5m11-5h-5v5" /></svg>
        {{ fullscreen ? 'Exit full screen' : 'Full screen' }}
      </RouterLink>
      </div>
    </header>
    <section class="game-stage" :class="{ 'in-play': game?.running, preparing: game?.preCountdownRunning, finished: gameOver }" aria-label="Game controls">
      <div class="game-center">
        <div class="game-message" aria-live="polite">
          <template v-if="game?.preCountdownRunning">
            <p class="stage-label">Get ready</p>
            <div class="pre-countdown-time" role="timer" aria-live="off" aria-label="Seconds until game starts">{{ game.preRemainingSeconds }}</div>
          </template>
          <h2 v-else class="stage-title">{{ title }}</h2>
        </div>
        <button class="game-action" :class="active ? 'stop' : 'start'" :disabled="!connected || locating || calibrating || mutation.isPending.value" @click="action">
          <svg aria-hidden="true" viewBox="0 0 24 24"><rect v-if="active" x="6" y="6" width="12" height="12" rx="1" /><path v-else d="m8 5 11 7-11 7z" /></svg>
          {{ active ? game?.preCountdownRunning ? 'Stop pre-countdown' : 'Stop Countdown' : 'Start Countdown' }}
        </button>
      </div>
    </section>
    <p v-if="mutation.error.value" role="alert" class="error-message">{{ mutation.error.value.message }}</p>
    <footer v-if="!fullscreen" class="game-footer"><RouterLink to="/countdown">Countdown settings</RouterLink></footer>
  </div>
</template>
