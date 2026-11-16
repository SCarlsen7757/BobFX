<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import { Eraser, ExternalLink, Info, LocateFixed, Plus, Radar, Trash2, X } from '@lucide/vue';
import { api } from '../lib/api';
import IconButton from '../components/IconButton.vue';
import RelativeTime from '../components/RelativeTime.vue';
import StatusMessage from '../components/StatusMessage.vue';
import { endpoints } from '../lib/endpoints';
import { useCanConfigure, useInstallationState } from '../lib/installation';
import { command, connected, useDiscovery } from '../lib/session';
import { usePageStatus } from '../lib/status';
import type { DeviceDetails } from '../lib/contracts';

const { data } = useDiscovery();
const discovery = computed(() => data.value?.value);
const { devices: registered, outputErrors } = useInstallationState();
const canConfigure = useCanConfigure();
const manualAddress = ref('');
const manualPort = ref(80);
const selected = ref('');
const dialog = ref<HTMLDialogElement>();
const scanButton = ref<HTMLButtonElement>();
const details = useQuery({ queryKey: computed(() => ['device', selected.value]), queryFn: () => api<DeviceDetails>(endpoints.discovery.device(selected.value)), enabled: computed(() => !!selected.value) });

// Discovery and the registered devices each have their own status line: a scan must not hide what a save said.
const scanning = usePageStatus();
const scan = scanning.action(async () => { await command(endpoints.discovery.scan); });
const clearDiscovered = scanning.action(async () => { await command(endpoints.discovery.devices, undefined, 'DELETE'); });
const forget = scanning.action(async (mac: string) => { await command(endpoints.discovery.device(mac), undefined, 'DELETE'); });
const scanBusy = computed(() => !connected.value || scanning.busy.value);
const scanStatus = computed(() => discovery.value?.error ?? (discovery.value?.scanning ? 'Scanning…' : discovery.value?.lastScanUtc ? 'Scan complete.' : ''));

const saving = usePageStatus();
const saved = 'Device settings saved.', located = 'Locate finished.';
const addManual = saving.action(async () => { await command(endpoints.installation.manualDevice, { address: manualAddress.value, port: manualPort.value }); return saved; });
const addSimulated = saving.action(async () => { await command(endpoints.installation.simulatedDevice); return 'Simulated controller added.'; });
const register = saving.action(async (mac: string) => { await command(endpoints.installation.deviceAction(mac, 'select')); return saved; });
const setSelected = saving.action(async (mac: string, use: boolean) => { await command(endpoints.installation.device(mac), { selected: use }, 'PUT'); return saved; });
const remove = saving.action(async (mac: string) => { await command(endpoints.installation.device(mac), undefined, 'DELETE'); return saved; });
const locate = saving.action(async (mac: string, discovered: boolean) => { await command(endpoints.installation.deviceAction(mac, discovered ? 'locate-discovered' : 'locate')); return located; });
const deviceBusy = computed(() => !canConfigure.value || saving.busy.value);

watch(selected, async value => { if (value) { await nextTick(); dialog.value?.showModal(); } });
function close() { dialog.value?.close(); selected.value = ''; scanButton.value?.focus(); }
function formatMac(mac: string) { return mac.match(/.{2}/g)?.join(':') ?? mac; }
const detailRows = computed(() => {
  const info = details.data.value?.details;
  return !info ? [] : [
    ['WiFi strength', `${info.wifiStrength ?? 'Unknown'} dBm`], ['LED count', info.ledCount], ['Brand', info.brand], ['Product', info.product],
    ['Architecture', info.architecture], ['Core version', info.coreVersion], ['Free heap', `${info.freeHeap} bytes`],
    ['Uptime', `${info.uptimeSeconds} seconds`], ['Data source type', info.dataSourceType], ['Data source address', info.dataSourceAddress],
  ] as const;
});
</script>
<template>
  <section class="settings-page devices-page">
    <h1 tabindex="-1">WLED devices</h1>
    <div class="device-toolbar" role="group" aria-label="Add device">
      <input id="manual-address" v-model="manualAddress" class="manual-address" aria-label="Private IPv4 address" title="Private IPv4 address" placeholder="IP address" inputmode="decimal" />
      <input id="manual-port" v-model.number="manualPort" class="manual-port" aria-label="HTTP port" title="HTTP port" type="number" min="1" max="65535" />
      <IconButton :icon="Plus" label="Check and add device" :disabled="deviceBusy" @click="addManual" />
      <button :disabled="deviceBusy" aria-label="Add simulated controller" title="Add simulated controller" @click="addSimulated"><Plus />Simulated</button>
    </div>
    <StatusMessage :status="saving" />
    <section v-if="registered.length" class="registered-section"><h2>Registered devices</h2><div class="registered-devices">
      <article v-for="device in registered" :key="device.mac" class="registered-device">
        <div class="registered-device-info">
          <label class="checkbox-label"><input type="checkbox" :checked="device.selected" :disabled="deviceBusy" @change="setSelected(device.mac, ($event.target as HTMLInputElement).checked)" />{{ device.name }}</label>
          <span>{{ device.simulated ? 'Simulated' : device.address }} · {{ device.ledCount }} LEDs</span>
          <span v-if="outputErrors[device.mac]" class="device-error">{{ outputErrors[device.mac] }}</span>
        </div>
        <div class="device-actions">
          <IconButton :icon="LocateFixed" :label="`Locate ${device.name}`" :disabled="deviceBusy" @click="locate(device.mac, false)" />
          <IconButton class="danger" :icon="Trash2" :label="`Remove ${device.name}`" :disabled="deviceBusy" @click="remove(device.mac)" />
        </div>
      </article>
    </div></section>
    <h2>Discovered devices</h2>
    <div class="device-toolbar">
      <button ref="scanButton" class="primary" :disabled="scanBusy" @click="scan"><Radar />Scan</button>
      <IconButton :icon="Eraser" label="Clear" :disabled="scanBusy" @click="clearDiscovered" />
      <span role="status">{{ scanStatus }}</span>
    </div>
    <p v-if="scanning.error.value" role="alert" class="error-message">{{ scanning.error.value }}</p>
    <p v-if="!discovery?.devices.length" class="preview-note">No devices found.</p>
    <div v-else class="table-scroll"><table>
      <thead><tr><th>Name</th><th>IP</th><th>Host name</th><th>MAC</th><th>Version</th><th>Port</th><th>Last seen</th><th><span class="sr-only">Status</span></th><th><span class="sr-only">Actions</span></th></tr></thead>
      <tbody><tr v-for="device in discovery.devices" :key="device.mac">
        <td>{{ device.name }}</td><td>{{ device.address }}</td><td>{{ device.hostName }}</td><td>{{ formatMac(device.mac) }}</td><td>{{ device.version }}</td><td>{{ device.port }}</td>
        <td><RelativeTime :value="device.lastSeen" /></td>
        <td><span class="device-status" :class="{ online: device.isOnline }" :title="device.isOnline ? 'Online' : 'Offline'"><span class="live-dot" aria-hidden="true"></span><span class="sr-only">{{ device.isOnline ? 'Online' : 'Offline' }}</span></span></td>
        <td class="device-actions">
          <template v-if="device.isOnline">
            <a class="control-link icon-button" :href="device.webUrl" target="_blank" rel="noopener noreferrer" :aria-label="`Web page of ${device.name}`" title="Web page"><ExternalLink /></a>
            <IconButton :icon="Info" :label="`Details of ${device.name}`" @click="selected = device.mac" />
            <IconButton :icon="Plus" :label="`Select ${device.name} for BobFX`" :disabled="deviceBusy" @click="register(device.mac)" />
            <IconButton :icon="LocateFixed" :label="`Locate ${device.name}`" :disabled="deviceBusy" @click="locate(device.mac, true)" />
          </template>
          <IconButton v-else class="danger" :icon="Trash2" :label="`Delete ${device.name}`" :disabled="scanBusy" @click="forget(device.mac)" />
        </td>
      </tr></tbody>
    </table></div>
    <dialog ref="dialog" aria-labelledby="device-title" @cancel.prevent="close" @close="selected = ''">
      <header class="dialog-header"><h2 id="device-title">{{ details.data.value?.device.name ?? 'Loading…' }}</h2><IconButton :icon="X" label="Close device details" @click="close" /></header>
      <p v-if="details.error.value" role="alert" class="error-message">{{ details.error.value.message }}</p>
      <dl v-else-if="detailRows.length" class="device-details"><template v-for="[label, value] in detailRows" :key="label"><dt>{{ label }}</dt><dd>{{ value || '—' }}</dd></template></dl>
      <p v-else-if="!details.isPending.value">No additional info available.</p>
    </dialog>
  </section>
</template>
