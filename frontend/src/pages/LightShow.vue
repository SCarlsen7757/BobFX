<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import { useMutation, useQuery } from '@tanstack/vue-query';
import { api } from '../lib/api';
import LightShowPreview from '../components/LightShowPreview.vue';
import { command, connected, useInstallation } from '../lib/session';
import { clone, useCanConfigure, validateLightShow } from '../lib/installation';
import type { AnimationSettings, LightShowColors, LightShowSegment, LightShowSettings, LightShowTheme } from '../lib/contracts';

const installation = useInstallation();
// A running test does not block this page: the next test replaces it and Save stops it first.
const lightsFree = useCanConfigure(true);
const { data: themes } = useQuery({ queryKey: ['light-show-themes'], queryFn: () => api<LightShowTheme[]>('/api/installation/light-show/themes') });
const draft = ref<{ lightShow: LightShowSettings; animation: AnimationSettings }>();
const message = ref('');
const advanced = ref(false);
const segments: Array<{ id: LightShowSegment; label: string }> = [
  { id: 'countdown', label: 'Countdown + GO' }, { id: 'playing', label: 'Playing' }, { id: 'gameOver', label: 'Game over' }];
const colorRoles: Array<{ key: keyof LightShowColors; label: string }> = [
  { key: 'primary', label: 'Main color' }, { key: 'secondary', label: 'Second color' },
  { key: 'tertiary', label: 'Third color' }, { key: 'accent', label: 'GO and accent color' }];
function reload() {
  const saved = installation.data.value?.value.settings;
  if (!saved) return;
  draft.value = clone({ lightShow: saved.lightShow, animation: saved.animation }); message.value = '';
}
watch(() => installation.data.value, () => { if (!draft.value) reload(); }, { immediate: true });
const test = computed(() => installation.data.value?.value.lightShowTest ?? null);
const hasBoards = computed(() => !!installation.data.value?.value.settings.boards.some(board => board.deviceMac));
// Only a test started from this page is stopped when the page is left.
let startedHere = false;
watch(test, value => { if (!value) startedHere = false; });
function swatch(colors: LightShowColors) {
  return { background: `linear-gradient(90deg, ${colors.tertiary}, ${colors.primary} 30%, ${colors.secondary} 60%, ${colors.tertiary} 82%, ${colors.accent} 82%)` };
}
const stopTest = () => command('/api/installation/light-show/test', undefined, 'DELETE');
const mutation = useMutation({ mutationFn: async (action: LightShowSegment | 'save' | 'stop') => {
  message.value = '';
  if (!draft.value) return;
  if (action === 'stop') { await stopTest(); return; }
  validateLightShow(draft.value.lightShow, draft.value.animation);
  if (action === 'save') {
    if (test.value) await stopTest();
    await command('/api/installation/light-show', draft.value, 'PUT');
    message.value = 'Light show saved.';
  } else {
    await command('/api/installation/light-show/test', { segment: action, ...draft.value });
    startedHere = true;
  }
} });
const busy = computed(() => !connected.value || mutation.isPending.value);
const blocked = computed(() => busy.value || !lightsFree.value);
const testStatus = computed(() => test.value
  ? `Playing “${segments.find(segment => segment.id === test.value?.segment)?.label}” on the boards. It stops by itself.`
  : !hasBoards.value ? 'Assign a controller to a board in Board layout to test on the boards.'
  : !lightsFree.value && connected.value ? 'The lights are in use. Tests are available when the game and other output are idle.' : '');

onBeforeRouteLeave(async () => {
  if (!startedHere || !test.value) return;
  try { await stopTest(); } catch { /* The test ends by itself if the connection is lost. */ }
});
function leaving() {
  if (startedHere && test.value)
    void fetch('/api/installation/light-show/test', { method: 'DELETE', credentials: 'same-origin', keepalive: true }).catch(() => {});
  startedHere = false;
}
window.addEventListener('pagehide', leaving);
onBeforeUnmount(() => { window.removeEventListener('pagehide', leaving); });
</script>

<template>
  <section class="settings-page light-show-page">
    <h1 tabindex="-1">Light show</h1>
    <p>How the boards light up during a game. The countdown, GO and game over follow the BUSY Bar; while the game is playing the boards drift slowly so the lights stay easy on the eyes but never stand still.</p>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
    <template v-if="draft">
      <fieldset class="settings-section" :disabled="busy"><legend>Look</legend>
        <div class="theme-options">
          <label v-for="theme in themes" :key="theme.id" class="theme-option">
            <input v-model="draft.lightShow.theme" type="radio" name="theme" :value="theme.id" /><span class="theme-swatch" :style="swatch(theme)"></span>{{ theme.name }}
          </label>
          <label class="theme-option">
            <input v-model="draft.lightShow.theme" type="radio" name="theme" value="custom" /><span class="theme-swatch" :style="swatch(draft.lightShow.custom)"></span>Custom
          </label>
        </div>
        <div v-if="draft.lightShow.theme === 'custom'" class="parameters">
          <div v-for="role in colorRoles" :key="role.key"><label :for="`color-${role.key}`">{{ role.label }}</label><input :id="`color-${role.key}`" v-model="draft.lightShow.custom[role.key]" type="color" /></div>
        </div>
        <div class="parameters slider-parameters">
          <div><label for="brightness">Brightness</label><input id="brightness" v-model.number="draft.lightShow.brightness" type="range" min="20" max="100" step="5" /><output for="brightness">{{ draft.lightShow.brightness }}%</output></div>
        </div>
      </fieldset>
      <fieldset class="settings-section" :disabled="busy"><legend>While the game is playing</legend>
        <div class="parameters slider-parameters">
          <div><label for="intensity">Intensity</label><select id="intensity" v-model="draft.lightShow.intensity"><option value="calm">Calm</option><option value="balanced">Balanced</option><option value="lively">Lively</option></select></div>
          <div><label for="drift-speed">Drift speed</label><input id="drift-speed" v-model.number="draft.lightShow.speed" type="range" min="25" max="200" step="5" /><output for="drift-speed">{{ draft.lightShow.speed }}%</output></div>
        </div>
      </fieldset>
      <fieldset class="settings-section" :disabled="busy"><legend>Game over</legend>
        <div class="parameters">
          <div><label for="flash-seconds">Flare seconds</label><input id="flash-seconds" v-model.number="draft.animation.flashSeconds" type="number" min="1" max="10" step="0.1" /></div>
          <div><label for="dot-seconds">Comet seconds</label><input id="dot-seconds" v-model.number="draft.animation.dotSeconds" type="number" min="1" max="30" step="0.1" /></div>
          <div><label for="dot-speed">Comet laps per second</label><input id="dot-speed" v-model.number="draft.animation.dotLapsPerSecond" type="number" min="1" max="20" step="0.1" /></div>
        </div>
      </fieldset>
      <details class="settings-section layout-advanced" :open="advanced" @toggle="advanced = ($event.target as HTMLDetailsElement).open">
        <summary>Advanced settings</summary>
        <fieldset :disabled="busy"><legend>Output</legend>
          <div class="parameters"><div><label for="fps">Frames per second</label><input id="fps" v-model.number="draft.animation.framesPerSecond" type="number" min="10" max="60" /></div></div>
        </fieldset>
      </details>
      <LightShowPreview :draft="draft" />
      <section class="settings-section" aria-label="Test on the boards">
        <h2>Test on the boards</h2>
        <p>Plays one part of the show on the real boards with the settings above, saved or not.</p>
        <div class="button-group">
          <button v-for="segment in segments" :key="segment.id" :disabled="blocked || !hasBoards" @click="mutation.mutate(segment.id)">{{ segment.label }}</button>
          <button class="danger" :disabled="busy || !test" @click="mutation.mutate('stop')">Stop test</button>
        </div>
        <p v-if="testStatus" class="test-status" role="status" data-testid="light-test-status">{{ testStatus }}</p>
      </section>
      <div class="button-group layout-save"><button class="primary" :disabled="blocked" @click="mutation.mutate('save')">Save light show</button><button :disabled="busy" @click="reload">Reload saved settings</button></div>
    </template>
  </section>
</template>
