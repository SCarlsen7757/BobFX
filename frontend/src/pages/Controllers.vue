<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue';
import { useMutation } from '@tanstack/vue-query';
import { api } from '../lib/api';
import { command, connected, useInstallation } from '../lib/session';
import { clone, newId, useCanConfigure } from '../lib/installation';
import type { RemoteController } from '../lib/contracts';
const installation = useInstallation();
const canConfigure = useCanConfigure();
const controllers = ref<RemoteController[]>();
const editing = ref<RemoteController>();
const password = ref('');
const pendingPasswords: Record<string, string> = {};
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
  password.value = ''; message.value = 'Controllers saved.';
} });
const busy = computed(() => !connected.value || mutation.isPending.value);
const passwordHint = computed(() => editing.value && installation.data.value?.value.passwordsSaved[editing.value.id]
  ? 'Password saved. Leave blank to keep it, or enter a replacement.' : 'Device API password');
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
        <button class="danger" :disabled="busy" @click="remove(bar.id)">Remove</button>
      </article>
    </div>
    <button :disabled="busy" @click="add">Add BUSY Bar</button>
    <fieldset v-if="editing" class="controller-editor" :disabled="busy"><legend>BUSY Bar settings</legend>
      <div class="parameters">
        <div><label for="controller-name">Name</label><input id="controller-name" v-model="editing.name" /></div>
        <div><label for="controller-address">Private IPv4 address</label><input id="controller-address" v-model="editing.address" inputmode="decimal" /></div>
        <div><label for="controller-port">HTTP port</label><input id="controller-port" v-model.number="editing.port" type="number" min="1" max="65535" /></div>
        <div><label for="controller-password">Device API password</label><input id="controller-password" v-model="password" type="password" autocomplete="new-password" maxlength="256" /></div>
      </div>
      <p v-if="installation.data.value?.value.passwordsSaved[editing.id]" class="password-status">{{ passwordHint }}</p>
      <label class="checkbox-label"><input v-model="editing.enabled" type="checkbox" />Enable integration</label>
      <label class="checkbox-label"><input v-model="editing.remoteEnabled" type="checkbox" />Enable START and wheel-press game controls</label>
      <div class="button-group"><button @click="apply">Apply controller settings</button><button @click="mutation.mutate('test')">Test connection</button></div>
    </fieldset>
    <div class="button-group"><button class="primary" :disabled="busy || !canConfigure" @click="mutation.mutate('save')">Save controllers</button><button :disabled="busy" @click="reload">Reload saved controllers</button></div>
  </section>
</template>
