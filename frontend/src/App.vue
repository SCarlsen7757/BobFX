<script setup lang="ts">
import { computed, nextTick, onErrorCaptured, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { RotateCcw, WifiOff } from '@lucide/vue';
import SunsetBackground from './components/SunsetBackground.vue';
import AudioControl from './components/AudioControl.vue';
import { BrowserAudio } from './lib/audio';
import { gamePath, pages } from './lib/routes';
import { connected, connectionStatus, startSession } from './lib/session';

const route = useRoute();
const immersive = computed(() => !!route.meta.fullscreen);
// Only the Game page keeps the moving background; every other page is a form laid straight on it.
const liveBackground = computed(() => route.path === gamePath || immersive.value);
const audioEnabled = ref(false);
const audioBusy = ref(false);
const audioReady = ref(false);
const audioStatus = ref('');
const fatalError = ref(false);
onErrorCaptured(error => { fatalError.value = true; console.error(error); return false; });
function reload() { location.reload(); }
let audio: BrowserAudio;
let stopSession: (() => void) | undefined;
onMounted(() => {
  audio = new BrowserAudio((enabled, status) => {
    audioEnabled.value = enabled;
    audioStatus.value = !enabled && status !== 'Audio muted.' ? status : '';
  });
  audioReady.value = true;
  stopSession = startSession(event => { void audio.play(event); });
});
onUnmounted(() => { stopSession?.(); audio?.dispose(); });
async function toggleAudio() {
  audioBusy.value = true;
  try { await audio.toggle(); } finally { audioBusy.value = false; }
}
watch(() => route.path, async () => {
  fatalError.value = false;
  await nextTick();
  document.querySelector<HTMLElement>('h1')?.focus({ preventScroll: true });
});
</script>

<template>
  <SunsetBackground :still="!liveBackground" />
  <div class="app-shell" :class="{ immersive }">
    <a class="skip-link" href="#main-content">Skip to content</a>
    <header v-if="!immersive" class="app-header">
      <RouterLink :to="gamePath" class="brand" aria-label="BobFX home">BobFX</RouterLink>
      <nav aria-label="Main navigation">
        <RouterLink v-for="page in pages" :key="page.path" :to="page.path" :aria-label="page.title" :title="page.title"><component :is="page.icon" /></RouterLink>
      </nav>
      <AudioControl :enabled="audioEnabled" :disabled="!audioReady || audioBusy" :status="audioStatus" @toggle="toggleAudio" />
    </header>
    <main id="main-content">
      <div v-if="!connected" class="connection-banner" role="status" data-testid="connection-status">
        <WifiOff />{{ connectionStatus === 'disconnected' ? 'Reconnecting…' : 'Connecting…' }}
      </div>
      <RouterView v-if="!fatalError" v-slot="{ Component }">
        <component :is="Component">
          <template #header-actions>
            <AudioControl v-if="immersive" :enabled="audioEnabled" :disabled="!audioReady || audioBusy" :status="audioStatus" @toggle="toggleAudio" />
          </template>
        </component>
      </RouterView>
      <section v-else class="error-message" role="alert"><h1>Unable to display this page</h1><button @click="reload"><RotateCcw />Reload</button></section>
    </main>
  </div>
</template>
