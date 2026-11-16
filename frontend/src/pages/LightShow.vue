<script setup lang="ts">
import { computed, watch } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import { useQuery } from '@tanstack/vue-query';
import { api, apiBeacon } from '../lib/api';
import LightShowPreview from '../components/LightShowPreview.vue';
import LightShowTestControls from '../components/LightShowTestControls.vue';
import LiveBoards from '../components/LiveBoards.vue';
import SaveBar from '../components/SaveBar.vue';
import StatusMessage from '../components/StatusMessage.vue';
import { useSectionDraft } from '../lib/draft';
import { endpoints } from '../lib/endpoints';
import { validateLightShow } from '../lib/installation';
import { useOnPageHide } from '../lib/lifecycle';
import { useLightShowTest } from '../lib/lightShowTest';
import { command, connected, requireLimits } from '../lib/session';
import { usePageStatus } from '../lib/status';
import type { LightShowColors, LightShowTheme } from '../lib/contracts';

const { data: themes } = useQuery({ queryKey: ['light-show-themes'], queryFn: () => api<LightShowTheme[]>(endpoints.installation.lightShowThemes), staleTime: Infinity });
// Asked first: staying on the page keeps a running test going.
const { draft, dirty, adopt } = useSectionDraft(settings => ({ lightShow: settings.lightShow, animation: settings.animation }));
const status = usePageStatus();
// A running test does not block this page: the next test replaces it and Save stops it first.
const { test, lightsFree, hasBoards, stop: stopTest } = useLightShowTest();
const colorRoles: Array<{ key: keyof LightShowColors; label: string }> = [
  { key: 'primary', label: 'Main color' }, { key: 'secondary', label: 'Second color' },
  { key: 'tertiary', label: 'Third color' }, { key: 'accent', label: 'GO and accent color' }];
function swatch(colors: LightShowColors) {
  return { background: `linear-gradient(90deg, ${colors.tertiary}, ${colors.primary} 30%, ${colors.secondary} 60%, ${colors.tertiary} 82%, ${colors.accent} 82%)` };
}
function reload() { adopt(); status.clear(); }
/** The draft, once it is known to be something the server will accept. */
function checked() {
  if (!draft.value) throw new Error('Reconnect before changing the light show.');
  validateLightShow(draft.value.lightShow, draft.value.animation, requireLimits());
  return draft.value;
}
const save = status.action(async () => {
  const show = checked();
  if (test.value) await stopTest();
  await command(endpoints.installation.lightShow, show, 'PUT');
  // Continue from exactly what was saved, so the page reads as saved whatever the server tidied up.
  adopt();
  return 'Light show saved.';
});
const busy = computed(() => !connected.value || status.busy.value);

// Only a test started from this page is stopped when the page is left.
let startedHere = false;
function started() { startedHere = true; }
watch(test, value => { if (!value) startedHere = false; });
onBeforeRouteLeave(async () => {
  if (!startedHere || !test.value) return;
  try { await stopTest(); } catch { /* The test ends by itself if the connection is lost. */ }
});
useOnPageHide(() => {
  if (startedHere && test.value) apiBeacon(endpoints.installation.lightShowTest, 'DELETE');
  startedHere = false;
});
</script>

<template>
  <section class="settings-page light-show-page">
    <h1 tabindex="-1">Light show</h1>
    <StatusMessage :status="status" />
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
      <fieldset class="settings-section" :disabled="busy"><legend>Playing</legend>
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
      <details class="settings-section layout-advanced">
        <summary>Advanced settings</summary>
        <fieldset :disabled="busy"><legend>Output</legend>
          <div class="parameters"><div><label for="fps">Frames per second</label><input id="fps" v-model.number="draft.animation.framesPerSecond" type="number" min="10" max="60" /></div></div>
        </fieldset>
      </details>
      <LightShowPreview :draft="draft" />
      <section class="settings-section" aria-label="Test on the boards">
        <h2>Test on the boards</h2>
        <LiveBoards v-if="hasBoards" />
        <LightShowTestControls :status="status" :show="checked" where=" on the boards" testid="light-test-status" @started="started" />
      </section>
      <SaveBar :dirty="dirty" label="Save light show" :disabled="busy || !lightsFree" reload-label="Reload saved settings" :reload-disabled="busy"
        :note="status.text.value" :failed="status.failed.value" @save="save" @reload="reload" />
    </template>
  </section>
</template>
