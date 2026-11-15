<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from 'vue';
import { useMutation } from '@tanstack/vue-query';
import { Check, PlugZap, Plus, Trash2 } from '@lucide/vue';
import { api } from '../lib/api';
import IconButton from '../components/IconButton.vue';
import SaveBar from '../components/SaveBar.vue';
import { useUnsavedGuard } from '../lib/unsaved';
import { command, connected, useInstallation } from '../lib/session';
import { clone, newId, useCanConfigure } from '../lib/installation';
import type { RemoteController } from '../lib/contracts';
const installation = useInstallation();
const canConfigure = useCanConfigure();
const controllers = ref<RemoteController[]>();
const editing = ref<RemoteController>();
const password = ref('');
const pendingPasswords = reactive<Record<string, string>>({});
const message = ref('');
function reload() {
  if (!installation.data.value) return;
  controllers.value = clone(installation.data.value.value.settings.controllers);
  editing.value = undefined; password.value = ''; message.value = '';
  for (const id of Object.keys(pendingPasswords)) delete pendingPasswords[id];
}
watch(() => installation.data.value, () => { if (!controllers.value) reload(); }, { immediate: true });
function edit(bar: RemoteController) { editing.value = clone(bar); password.value = ''; }
function add() { edit({ id: newId(), name: 'BUSY Bar', address: '', port: 80, enabled: false, remoteEnabled: false, secretName: 'busybar_token' }); }
function remove(id: string) {
  controllers.value = controllers.value?.filter(bar => bar.id !== id);
  delete pendingPasswords[id];
  if (editing.value?.id === id) { editing.value = undefined; password.value = ''; }
}
function apply() {
  if (!editing.value) return true;
  const bar = editing.value;
  if (!bar.name.trim() || !bar.address.trim() || !Number.isInteger(bar.port) || bar.port < 1 || bar.port > 65535) {
    message.value = 'Enter a name, private IPv4 address and valid HTTP port.'; return false;
  }
  if (password.value && (password.value.length > 256 || /[^\x20-\x7e]/.test(password.value))) {
    message.value = 'Enter a device API password of 1–256 printable ASCII characters.'; return false;
  }
  const index = controllers.value?.findIndex(c => c.id === bar.id) ?? -1;
  if (index < 0) controllers.value?.push(clone(bar)); else controllers.value!.splice(index, 1, clone(bar));
  if (password.value) { pendingPasswords[bar.id] = password.value; password.value = ''; }
  message.value = 'Controller settings applied.'; return true;
}
const mutation = useMutation({ mutationFn: async (action: 'save' | 'test') => {
  message.value = '';
  if (!apply()) return;
  if (action === 'test' && editing.value) {
    const result = await api<{ apiVersion: string }>('/api/installation/controllers/test', 'POST', { controller: editing.value, password: pendingPasswords[editing.value.id] });
    message.value = `Connection successful. BUSY Bar API ${result.apiVersion}.`; return;
  }
  await command('/api/installation/controllers', { controllers: controllers.value, passwords: pendingPasswords }, 'PUT');
  for (const id of Object.keys(pendingPasswords)) delete pendingPasswords[id];
  // Continue from exactly what was saved, with the editor still open on the same controller.
  const saved = installation.data.value?.value.settings.controllers;
  if (saved) {
    controllers.value = clone(saved);
    const open = saved.find(bar => bar.id === editing.value?.id);
    if (open) editing.value = clone(open);
  }
  password.value = ''; message.value = 'Controllers saved.';
} });
const busy = computed(() => !connected.value || mutation.isPending.value);
// What Save would send differs from what is saved: the list with the open editor's values, or a new password.
const dirty = computed(() => {
  const saved = installation.data.value?.value.settings.controllers;
  if (!controllers.value || !saved) return false;
  const open = editing.value;
  const merged = !open ? controllers.value : controllers.value.some(bar => bar.id === open.id)
    ? controllers.value.map(bar => bar.id === open.id ? open : bar) : [...controllers.value, open];
  return JSON.stringify(merged) !== JSON.stringify(saved) || !!password.value || Object.keys(pendingPasswords).length > 0;
});
useUnsavedGuard(dirty);
const passwordSaved = computed(() => !!editing.value && !!installation.data.value?.value.passwordsSaved[editing.value.id]);
onUnmounted(() => { password.value = ''; for (const id of Object.keys(pendingPasswords)) delete pendingPasswords[id]; });
</script>
<template>
  <section class="settings-page controllers-page">
    <h1 tabindex="-1">Remote controllers</h1>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
    <div class="registered-devices">
      <article v-for="bar in controllers" :key="bar.id" class="registered-device">
        <div class="controller-summary"><button :disabled="busy" @click="edit(bar)">{{ bar.name }} · {{ bar.address }} · {{ bar.enabled ? 'Enabled' : 'Disabled' }}</button>
          <p v-if="installation.data.value?.value.controllers[bar.id]" class="controller-status">{{ installation.data.value.value.controllers[bar.id]?.state }}<span v-if="installation.data.value.value.controllers[bar.id]?.apiVersion"> · API {{ installation.data.value.value.controllers[bar.id]?.apiVersion }}</span></p>
        </div>
        <IconButton class="danger" :icon="Trash2" label="Remove" :disabled="busy" @click="remove(bar.id)" />
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
        <button aria-label="Test connection" title="Test connection" @click="mutation.mutate('test')"><PlugZap />Test</button>
      </div>
    </fieldset>
    <SaveBar :dirty="dirty" label="Save controllers" :disabled="busy || !canConfigure" reload-label="Reload saved controllers" :reload-disabled="busy"
      :note="mutation.error.value?.message ?? message" :failed="!!mutation.error.value" @save="mutation.mutate('save')" @reload="reload" />
  </section>
</template>
