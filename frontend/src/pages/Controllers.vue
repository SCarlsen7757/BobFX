<script setup lang="ts">
import { computed, onUnmounted, reactive, ref } from 'vue';
import { Check, PlugZap, Plus, Trash2 } from '@lucide/vue';
import { api } from '../lib/api';
import IconButton from '../components/IconButton.vue';
import SaveBar from '../components/SaveBar.vue';
import StatusMessage from '../components/StatusMessage.vue';
import { useSectionDraft } from '../lib/draft';
import { endpoints } from '../lib/endpoints';
import { clone, isIntBetween, newId, useCanConfigure, useInstallationState } from '../lib/installation';
import { command, connected } from '../lib/session';
import { usePageStatus } from '../lib/status';
import type { RemoteController } from '../lib/contracts';

const { controllerStatus, passwordsSaved } = useInstallationState();
const canConfigure = useCanConfigure();
// The controller in the editor is a copy; Apply puts it in the list and Save sends the list.
const editing = ref<RemoteController>();
const password = ref('');
// Passwords are sent with Save and never read back, so they are kept apart from the settings draft.
const pendingPasswords = reactive<Record<string, string>>({});
function forgetPasswords() { password.value = ''; for (const id of Object.keys(pendingPasswords)) delete pendingPasswords[id]; }
/** The list with the open editor's values in it: what Save would send. */
function withEditor(list: RemoteController[]) {
  const open = editing.value;
  return !open ? list : list.some(bar => bar.id === open.id) ? list.map(bar => bar.id === open.id ? open : bar) : [...list, open];
}
const { draft: controllers, dirty, adopt } = useSectionDraft(settings => settings.controllers,
  { value: withEditor, also: () => !!password.value || Object.keys(pendingPasswords).length > 0 });
const status = usePageStatus();

function reload() { adopt(); editing.value = undefined; forgetPasswords(); status.clear(); }
function edit(bar: RemoteController) { editing.value = clone(bar); password.value = ''; }
function add() { edit({ id: newId(), name: 'BUSY Bar', address: '', port: 80, enabled: false, remoteEnabled: false, secretName: 'busybar_token' }); }
function remove(id: string) {
  controllers.value = controllers.value?.filter(bar => bar.id !== id);
  delete pendingPasswords[id];
  if (editing.value?.id === id) { editing.value = undefined; password.value = ''; }
}
/** What is wrong with the editor's values; empty when they can be applied. */
function problem() {
  const bar = editing.value;
  if (!bar) return '';
  if (!bar.name.trim() || !bar.address.trim() || !isIntBetween(bar.port, 1, 65535)) return 'Enter a name, private IPv4 address and valid HTTP port.';
  if (password.value && (password.value.length > 256 || /[^\x20-\x7e]/.test(password.value))) return 'Enter a device API password of 1–256 printable ASCII characters.';
  return '';
}
/** Puts the editor's values in the list. Throws what is wrong with them instead, when something is. */
function applyEditor() {
  const wrong = problem();
  if (wrong) throw new Error(wrong);
  if (!editing.value || !controllers.value) return;
  controllers.value = withEditor(controllers.value).map(bar => clone(bar));
  if (password.value) { pendingPasswords[editing.value.id] = password.value; password.value = ''; }
}
const apply = status.action(() => { applyEditor(); return 'Controller settings applied.'; });
// A test tries the values in the editor as they are, without applying them to the list.
const test = status.action(async () => {
  const bar = editing.value, wrong = problem();
  if (wrong) throw new Error(wrong);
  if (!bar) return;
  const result = await api<{ apiVersion: string }>(endpoints.installation.controllerTest, 'POST', { controller: bar, password: password.value || pendingPasswords[bar.id] });
  return `Connection successful. BUSY Bar API ${result.apiVersion}.`;
});
const save = status.action(async () => {
  applyEditor();
  await command(endpoints.installation.controllers, { controllers: controllers.value, passwords: pendingPasswords }, 'PUT');
  forgetPasswords();
  // Continue from exactly what was saved, with the editor still open on the same controller.
  adopt();
  const open = controllers.value?.find(bar => bar.id === editing.value?.id);
  if (open) editing.value = clone(open);
  return 'Controllers saved.';
});
const busy = computed(() => !connected.value || status.busy.value);
const passwordSaved = computed(() => !!editing.value && !!passwordsSaved.value[editing.value.id]);
onUnmounted(forgetPasswords);
</script>
<template>
  <section class="settings-page controllers-page">
    <h1 tabindex="-1">Remote controllers</h1>
    <StatusMessage :status="status" />
    <div class="registered-devices">
      <article v-for="bar in controllers" :key="bar.id" class="registered-device">
        <div class="controller-summary"><button :disabled="busy" @click="edit(bar)">{{ bar.name }} · {{ bar.address }} · {{ bar.enabled ? 'Enabled' : 'Disabled' }}</button>
          <p v-if="controllerStatus[bar.id]" class="controller-status">{{ controllerStatus[bar.id]?.state }}<span v-if="controllerStatus[bar.id]?.apiVersion"> · API {{ controllerStatus[bar.id]?.apiVersion }}</span></p>
        </div>
        <IconButton class="danger" :icon="Trash2" :label="`Remove ${bar.name}`" :disabled="busy" @click="remove(bar.id)" />
      </article>
    </div>
    <button :disabled="busy" aria-label="Add BUSY Bar" title="Add BUSY Bar" @click="add"><Plus />BUSY Bar</button>
    <fieldset v-if="editing" class="controller-editor" :disabled="busy"><legend>BUSY Bar settings</legend>
      <div class="parameters">
        <div><label for="controller-name">Name</label><input id="controller-name" v-model="editing.name" /></div>
        <div><label for="controller-address">Private IPv4 address</label><input id="controller-address" v-model="editing.address" inputmode="decimal" /></div>
        <div><label for="controller-port">HTTP port</label><input id="controller-port" v-model.number="editing.port" type="number" min="1" max="65535" /></div>
        <div><label for="controller-password">Device API password</label><input id="controller-password" v-model="password" type="password" autocomplete="new-password" maxlength="256" :placeholder="passwordSaved ? 'Saved · blank keeps it' : ''" /></div>
      </div>
      <label class="checkbox-label"><input v-model="editing.enabled" type="checkbox" />Enable integration</label>
      <label class="checkbox-label"><input v-model="editing.remoteEnabled" type="checkbox" />Enable START and wheel-press game controls</label>
      <div class="button-group">
        <button aria-label="Apply controller settings" title="Apply controller settings" @click="apply"><Check />Apply</button>
        <button aria-label="Test connection" title="Test connection" @click="test"><PlugZap />Test</button>
      </div>
    </fieldset>
    <SaveBar :dirty="dirty" label="Save controllers" :disabled="busy || !canConfigure" reload-label="Reload saved controllers" :reload-disabled="busy"
      :note="status.text.value" :failed="status.failed.value" @save="save" @reload="reload" />
  </section>
</template>
