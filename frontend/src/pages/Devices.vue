<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { useMutation, useQuery } from '@tanstack/vue-query';
import { Eraser, ExternalLink, Info, LocateFixed, Plus, Radar, Trash2, X } from '@lucide/vue';
import { api } from '../lib/api';
import IconButton from '../components/IconButton.vue';
import { command, connected, useDiscovery, useInstallation } from '../lib/session';
import { useCanConfigure } from '../lib/installation';
import type { DeviceDetails } from '../lib/contracts';
const { data } = useDiscovery();
const discovery = computed(() => data.value?.value);
const installation = useInstallation();
const canConfigure = useCanConfigure();
const manualAddress = ref('');
const manualPort = ref(80);
const deviceStatus = ref('');
const selected = ref('');
const dialog = ref<HTMLDialogElement>();
const scanButton = ref<HTMLButtonElement>();
const details = useQuery({ queryKey: computed(() => ['device', selected.value]), queryFn: () => api<DeviceDetails>(`/api/wled/devices/${selected.value}`), enabled: computed(() => !!selected.value) });
const mutation = useMutation({ mutationFn: async (action: string) => {
  if (action === 'scan') await command('/api/wled/scan');
  else await command(`/api/wled/devices${action === 'clear' ? '' : `/${action}`}`, undefined, 'DELETE');
} });
const busy = computed(() => !connected.value || mutation.isPending.value);
const deviceMutation = useMutation({ mutationFn: async (action: { kind: 'manual' | 'simulated' | 'select' | 'toggle' | 'remove' | 'locate' | 'locate-discovered'; mac?: string; selected?: boolean }) => {
  deviceStatus.value = '';
  const base = '/api/installation/devices';
  if (action.kind === 'manual') await command(`${base}/manual`, { address: manualAddress.value, port: manualPort.value });
  else if (action.kind === 'simulated') { await command(`${base}/simulated`); deviceStatus.value = 'Simulated controller added.'; return; }
  else if (action.kind === 'toggle') await command(`${base}/${action.mac}`, { selected: action.selected }, 'PUT');
  else if (action.kind === 'remove') await command(`${base}/${action.mac}`, undefined, 'DELETE');
  else await command(`${base}/${action.mac}/${action.kind}`);
  deviceStatus.value = action.kind.startsWith('locate') ? 'Locate finished.' : 'Device settings saved.';
} });
const deviceBusy = computed(() => !canConfigure.value || deviceMutation.isPending.value);
watch(selected, async value => { if (value) { await nextTick(); dialog.value?.showModal(); } });
function close() { dialog.value?.close(); selected.value = ''; scanButton.value?.focus(); }
const now = ref(Date.now());
let seenTimer: ReturnType<typeof setInterval> | undefined;
onMounted(() => { seenTimer = setInterval(() => { now.value = Date.now(); }, 1000); });
onUnmounted(() => { clearInterval(seenTimer); });
function seenDate(value: string) { return new Date(/(?:z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}Z`); }
function seen(value: string) {
  const elapsed = Math.max(0, Math.floor((now.value - seenDate(value).getTime()) / 1000));
  if (!Number.isFinite(elapsed)) return 'Unknown';
  if (elapsed < 5) return 'Just now';
  const [amount, unit] = elapsed < 60 ? [elapsed, 'second'] : elapsed < 3600 ? [Math.floor(elapsed / 60), 'minute']
    : elapsed < 86400 ? [Math.floor(elapsed / 3600), 'hour'] : [Math.floor(elapsed / 86400), 'day'];
  return `${amount} ${unit}${amount === 1 ? '' : 's'} ago`;
}
</script>
<template>
  <section class="settings-page devices-page">
    <h1 tabindex="-1">WLED devices</h1>
    <div class="device-toolbar" role="group" aria-label="Add device">
      <input id="manual-address" v-model="manualAddress" class="manual-address" aria-label="Private IPv4 address" title="Private IPv4 address" placeholder="IP address" inputmode="decimal" />
      <input id="manual-port" v-model.number="manualPort" class="manual-port" aria-label="HTTP port" title="HTTP port" type="number" min="1" max="65535" />
      <IconButton :icon="Plus" label="Check and add device" :disabled="deviceBusy" @click="deviceMutation.mutate({ kind: 'manual' })" />
      <button :disabled="deviceBusy" aria-label="Add simulated controller" title="Add simulated controller" @click="deviceMutation.mutate({ kind: 'simulated' })"><Plus />Simulated</button>
    </div>
    <p v-if="deviceStatus || deviceMutation.error.value" role="status" :class="{ 'error-message': deviceMutation.error.value }">{{ deviceMutation.error.value?.message ?? deviceStatus }}</p>
    <section v-if="installation.data.value?.value.settings.devices.length" class="registered-section"><h2>Registered devices</h2><div class="registered-devices">
      <article v-for="saved in installation.data.value.value.settings.devices" :key="saved.mac" class="registered-device">
        <div class="registered-device-info"><label class="checkbox-label"><input type="checkbox" :checked="saved.selected" :disabled="deviceBusy" @change="deviceMutation.mutate({ kind: 'toggle', mac: saved.mac, selected: ($event.target as HTMLInputElement).checked })" />{{ saved.name }}</label><span>{{ saved.simulated ? 'Simulated' : saved.address }} · {{ saved.ledCount }} LEDs</span><span v-if="installation.data.value.value.outputErrors[saved.mac]" class="device-error">{{ installation.data.value.value.outputErrors[saved.mac] }}</span></div>
        <div class="device-actions"><IconButton :icon="LocateFixed" :label="`Locate ${saved.name}`" :disabled="deviceBusy" @click="deviceMutation.mutate({ kind: 'locate', mac: saved.mac })" /><IconButton class="danger" :icon="Trash2" label="Remove device" :disabled="deviceBusy" @click="deviceMutation.mutate({ kind: 'remove', mac: saved.mac })" /></div>
      </article>
    </div></section>
    <h2>Discovered devices</h2>
    <div class="device-toolbar"><button ref="scanButton" class="primary" :disabled="busy" @click="mutation.mutate('scan')"><Radar />Scan</button><IconButton :icon="Eraser" label="Clear" :disabled="busy" @click="mutation.mutate('clear')" /><span role="status">{{ discovery?.error ?? (discovery?.scanning ? 'Scanning…' : discovery?.lastScanUtc ? 'Scan complete.' : '') }}</span></div>
    <p v-if="mutation.error.value" role="alert" class="error-message">{{ mutation.error.value.message }}</p>
    <p v-if="!discovery?.devices.length" class="preview-note">No devices found.</p>
    <div v-else class="table-scroll"><table><thead><tr><th>Name</th><th>IP</th><th>Host name</th><th>MAC</th><th>Version</th><th>Port</th><th>Last seen</th><th><span class="sr-only">Status</span></th><th><span class="sr-only">Actions</span></th></tr></thead><tbody><tr v-for="device in discovery.devices" :key="device.mac"><td>{{ device.name }}</td><td>{{ device.address }}</td><td>{{ device.hostName }}</td><td>{{ device.mac.match(/.{2}/g)?.join(':') }}</td><td>{{ device.version }}</td><td>{{ device.port }}</td><td><time :datetime="device.lastSeen" :title="seenDate(device.lastSeen).toLocaleString()">{{ seen(device.lastSeen) }}</time></td><td><span class="device-status" :class="{ online: device.isOnline }" :title="device.isOnline ? 'Online' : 'Offline'"><span class="live-dot" aria-hidden="true"></span><span class="sr-only">{{ device.isOnline ? 'Online' : 'Offline' }}</span></span></td><td class="device-actions"><template v-if="device.isOnline"><a class="control-link icon-button" :href="device.webUrl" target="_blank" rel="noopener noreferrer" aria-label="Web page" title="Web page"><ExternalLink /></a><IconButton :icon="Info" label="Details" @click="selected = device.mac" /><IconButton :icon="Plus" label="Select for BobFX" :disabled="deviceBusy" @click="deviceMutation.mutate({ kind: 'select', mac: device.mac })" /><IconButton :icon="LocateFixed" label="Locate" :disabled="deviceBusy" @click="deviceMutation.mutate({ kind: 'locate-discovered', mac: device.mac })" /></template><IconButton v-else class="danger" :icon="Trash2" label="Delete" :disabled="busy" @click="mutation.mutate(device.mac)" /></td></tr></tbody></table></div>
    <dialog ref="dialog" aria-labelledby="device-title" @cancel.prevent="close" @close="selected = ''">
      <header class="dialog-header"><h2 id="device-title">{{ details.data.value?.device.name ?? 'Loading…' }}</h2><IconButton :icon="X" label="Close device details" @click="close" /></header>
      <p v-if="details.error.value" role="alert" class="error-message">{{ details.error.value.message }}</p>
      <dl v-else-if="details.data.value?.details" class="device-details"><template v-for="(value, label) in { 'WiFi strength': `${details.data.value.details.wifiStrength ?? 'Unknown'} dBm`, 'LED count': details.data.value.details.ledCount, Brand: details.data.value.details.brand, Product: details.data.value.details.product, Architecture: details.data.value.details.architecture, 'Core version': details.data.value.details.coreVersion, 'Free heap': `${details.data.value.details.freeHeap} bytes`, Uptime: `${details.data.value.details.uptimeSeconds} seconds`, 'Data source type': details.data.value.details.dataSourceType, 'Data source address': details.data.value.details.dataSourceAddress }" :key="label"><dt>{{ label }}</dt><dd>{{ value || '—' }}</dd></template></dl>
      <p v-else-if="!details.isPending.value">No additional info available.</p>
    </dialog>
  </section>
</template>
