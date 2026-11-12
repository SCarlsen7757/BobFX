<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import { useMutation } from '@tanstack/vue-query';
import { api } from '../lib/api';
import { command, connected, synchronize, useInstallation } from '../lib/session';
import { boardPixels, clone, edgeStrips, newId, swapStrips, twoStripEdges, unevenStripNotice, unmappedLeds, useCanConfigure, validateBoards } from '../lib/installation';
import type { Board, CalibrationSession, InstallationSettings } from '../lib/contracts';

const installation = useInstallation();
const canConfigure = useCanConfigure();
const draft = ref<InstallationSettings>();
const selectedId = ref('');
const boardRow = ref<HTMLElement>();
const editing = computed(() => draft.value?.boards.find(b => b.id === selectedId.value));
const assigned = computed(() => draft.value?.boards.filter(b => b.deviceMac !== null) ?? []);
const selectedPosition = computed(() => draft.value?.boards.findIndex(b => b.id === selectedId.value) ?? -1);
const step = computed(() => assigned.value.findIndex(b => b.id === selectedId.value));
const sessionId = ref<string>();
const message = ref('');
const advanced = ref(false);
const edgeNames = ['Top', 'Right', 'Bottom', 'Left'];
const edgeColors = ['#0060a0', '#008040', '#a06000', '#900060'];
const stripColors = ['#ffad66', '#b18cff', '#96eddf', '#ff4fad'];
function stripCount(board: Board) { return new Set(edgeStrips(board.edges).filter(strip => strip >= 0)).size; }
function mappingLabel(board: Board) {
  if (!board.deviceMac) return 'Unassigned';
  const strips = stripCount(board);
  return strips === 0 ? 'Not mapped' : strips === 1 ? '1 strip' : `${strips} strips`;
}
function spareLabel(board: Board) {
  const device = draft.value?.devices.find(d => d.mac === board.deviceMac);
  const spare = device && board.edges.length ? unmappedLeds(board, device.ledCount) : 0;
  return spare === 0 ? '' : spare === 1 ? '1 LED unmapped' : `${spare} LEDs unmapped`;
}
const legendStrips = computed(() => Math.max(0, ...(draft.value?.boards.filter(b => b.deviceMac).map(stripCount) ?? [])));
let beforeCalibration: InstallationSettings | undefined;
let heartbeat: ReturnType<typeof setInterval> | undefined;
let previewQueue: Promise<void> = Promise.resolve();

function reload() {
  if (!installation.data.value || sessionId.value) return;
  draft.value = clone(installation.data.value.value.settings);
  selectedId.value = draft.value.boards[0]?.id ?? ''; message.value = '';
}
function clearSession() { clearInterval(heartbeat); heartbeat = undefined; sessionId.value = undefined; }
function select(board: Board) {
  if (sessionId.value && !board.deviceMac) return;
  selectedId.value = board.id; preview();
}
function add() {
  if (!draft.value) return;
  let number = draft.value.boards.length + 1;
  while (draft.value.boards.some(b => b.name === `Board ${number}`)) number++;
  const board: Board = { id: newId(), name: `Board ${number}`, deviceMac: null, rotation: 0, edges: [] };
  draft.value.boards.push(board); selectedId.value = board.id;
}
function choices(board: Board) {
  return draft.value?.devices.filter(d => d.selected && !draft.value?.boards.some(b => b.id !== board.id && b.deviceMac === d.mac)) ?? [];
}
function assign(board: Board, value: string) {
  if (!draft.value) return;
  const device = draft.value.devices.find(d => d.mac === value);
  board.deviceMac = device?.mac ?? null;
  message.value = '';
  if (!device) return;
  const fits = board.edges.length === 4 && board.edges.every(e => e.start >= 0 && e.count >= 1 && e.start + e.count <= device.ledCount);
  if (!fits) {
    try { board.edges = twoStripEdges(device.ledCount); message.value = unevenStripNotice(device.name, device.ledCount); }
    catch (error) { board.edges = []; advanced.value = true; selectedId.value = board.id; message.value = (error as Error).message; }
  }
}
function move(index: number, delta: number) {
  const boards = draft.value?.boards;
  if (!boards || index + delta < 0 || index + delta >= boards.length) return;
  [boards[index], boards[index + delta]] = [boards[index + delta]!, boards[index]!]; preview();
}
function remove(id: string) {
  if (!draft.value) return;
  draft.value.boards = draft.value.boards.filter(b => b.id !== id);
  if (selectedId.value === id) selectedId.value = draft.value.boards[0]?.id ?? '';
}
function rotate(delta: number) {
  if (editing.value) { editing.value.rotation = (editing.value.rotation + delta + 360) % 360; preview(); }
}
function swap() { if (editing.value?.edges.length === 4) { swapStrips(editing.value); preview(); } }
function next(delta: number) { const board = assigned.value[step.value + delta]; if (board) select(board); }
function manualMapping() {
  if (editing.value) editing.value.edges = Array.from({ length: 4 }, (_, side) => ({ start: side, count: 1, reversed: false }));
}
function defaultMapping() {
  try {
    const device = draft.value?.devices.find(d => d.mac === editing.value?.deviceMac);
    if (editing.value && device) editing.value.edges = twoStripEdges(device.ledCount);
    message.value = device ? unevenStripNotice(device.name, device.ledCount) : '';
  } catch (error) { message.value = (error as Error).message; }
}
function previewRequest() {
  if (!draft.value || !editing.value) throw new Error('Choose a board first.');
  validateBoards(draft.value);
  return { boards: clone(draft.value.boards), activeBoardId: editing.value.id };
}
async function calibrationCommand<T = void>(path: string, method: string, body?: unknown) {
  const abort = new AbortController();
  const timeout = setTimeout(() => abort.abort(), 5000);
  try { return await api<T>(path, method, body, abort.signal); }
  finally { clearTimeout(timeout); }
}
function sendPreview() {
  const id = sessionId.value;
  previewQueue = previewQueue.catch(() => {}).then(async () => {
    if (id && sessionId.value === id && connected.value && !document.hidden)
      await calibrationCommand(`/api/installation/calibration/${id}`, 'PUT', previewRequest());
  });
  return previewQueue;
}
function preview() {
  if (!sessionId.value) return;
  void sendPreview().catch(error => { message.value = `Preview could not be updated: ${(error as Error).message}`; void synchronize(); });
}
async function stopCalibration() {
  const id = sessionId.value;
  if (!id) return;
  clearInterval(heartbeat);
  await previewQueue.catch(() => {});
  await calibrationCommand(`/api/installation/calibration/${id}`, 'DELETE');
  clearSession();
  await synchronize();
}
async function save() {
  if (!draft.value) return;
  validateBoards(draft.value);
  const idleSync = { ...draft.value.idleSync, followers: draft.value.idleSync.followers.filter(mac => mac !== draft.value!.idleSync.masterMac) };
  await command('/api/installation/layout', { boards: draft.value.boards, idleSync }, 'PUT');
  message.value = Object.keys(installation.data.value?.value.outputErrors ?? {}).length ? 'Layout saved; check device errors.' : 'Layout saved.';
}
const mutation = useMutation({ mutationFn: async (action: 'save' | 'start' | 'finish' | 'cancel' | 'locate') => {
  message.value = '';
  if (action === 'save') await save();
  else if (action === 'locate') { await command(`/api/installation/devices/${editing.value?.deviceMac}/locate`); message.value = 'Locate finished.'; }
  else if (action === 'start') {
    if (!editing.value?.deviceMac) selectedId.value = assigned.value[0]?.id ?? '';
    const request = previewRequest();
    beforeCalibration = clone(draft.value!);
    const session = await calibrationCommand<CalibrationSession>('/api/installation/calibration', 'POST', request);
    sessionId.value = session.sessionId;
    heartbeat = setInterval(preview, 5000);
    await synchronize();
  } else {
    await stopCalibration();
    if (action === 'finish') await save();
    else { if (beforeCalibration) draft.value = clone(beforeCalibration); message.value = 'Calibration cancelled. Earlier draft restored.'; }
  }
} });
const busy = computed(() => !connected.value || mutation.isPending.value || (!canConfigure.value && !sessionId.value));
const setupBusy = computed(() => busy.value || !!sessionId.value);
watch([selectedId, selectedPosition], async () => {
  await nextTick();
  const row = boardRow.value;
  const card = Array.from(row?.querySelectorAll<HTMLElement>('.board-item') ?? []).find(item => item.dataset.boardId === selectedId.value);
  if (!row || !card) return;
  const container = row.getBoundingClientRect(), item = card.getBoundingClientRect();
  if (item.left < container.left) row.scrollLeft += item.left - container.left - 4;
  else if (item.right > container.right) row.scrollLeft += item.right - container.right + 4;
});
watch(() => installation.data.value, () => {
  if (!draft.value) reload();
  if (sessionId.value && !mutation.isPending.value && installation.data.value?.value.calibration?.sessionId !== sessionId.value) {
    clearSession(); message.value = 'Calibration ended. Your changes are still here; save them or start calibration again.';
  }
}, { immediate: true });

onBeforeRouteLeave(async () => {
  try { await stopCalibration(); }
  catch { clearSession(); /* The server lease releases output if navigation loses the connection. */ }
});
function leaving() {
  const id = sessionId.value;
  clearSession();
  if (id) void fetch(`/api/installation/calibration/${id}`, { method: 'DELETE', credentials: 'same-origin', keepalive: true }).catch(() => {});
}
window.addEventListener('pagehide', leaving);
onBeforeUnmount(() => { window.removeEventListener('pagehide', leaving); leaving(); });
</script>

<template>
  <section class="settings-page layout-page">
    <h1 tabindex="-1">Board layout</h1>
    <p>Add your boards from left to right, then assign a WLED controller to each one. Unassigned boards can be saved and are skipped during games.</p>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
    <p v-if="installation.data.value?.value.calibration && !sessionId" role="status">Boards are being calibrated in another window.</p>
    <template v-if="draft">
      <div class="layout-toolbar button-group">
        <button class="primary" :disabled="setupBusy" @click="add">Add board</button>
        <button :disabled="setupBusy || !assigned.length" @click="mutation.mutate('start')">Calibrate boards</button>
        <RouterLink to="/wled">Manage WLED devices</RouterLink>
      </div>
      <p v-if="!draft.boards.length" class="empty-state">Add your first board. You can assign its controller later.</p>
      <div ref="boardRow" class="board-row" aria-label="Boards from left to right" tabindex="0">
        <article v-for="(board, index) in draft.boards" :key="board.id" :data-board-id="board.id" class="board-item" :class="{ 'unassigned-board': !board.deviceMac }">
          <button class="board-tile" :class="{ selected: selectedId === board.id }" :aria-pressed="selectedId === board.id" :disabled="busy || (!!sessionId && !board.deviceMac)" @click="select(board)">
            <svg viewBox="0 0 200 200" role="img" :aria-label="`${board.name}, rotation ${board.rotation} degrees`">
              <rect x="10" y="10" width="180" height="180" rx="3" fill="none" stroke="var(--border)" stroke-width="4" />
              <template v-if="sessionId && selectedId === board.id">
                <path d="M10 10H190" :stroke="edgeColors[0]" stroke-width="8" /><path d="M190 10V190" :stroke="edgeColors[1]" stroke-width="8" />
                <path d="M190 190H10" :stroke="edgeColors[2]" stroke-width="8" /><path d="M10 190V10" :stroke="edgeColors[3]" stroke-width="8" />
              </template>
              <template v-else v-for="(pixel, pixelIndex) in boardPixels(board)" :key="pixelIndex">
                <circle v-if="pixel.stripStart" class="strip-start" :cx="pixel.x" :cy="pixel.y" r="6" fill="none" :stroke="stripColors[pixel.strip % 4]" stroke-width="1.5" />
                <circle :cx="pixel.x" :cy="pixel.y" r="2.5" :fill="stripColors[pixel.strip % 4]"><title>Strip {{ pixel.strip + 1 }} · LED {{ pixel.index }}{{ pixel.stripStart ? ' (first LED)' : '' }}</title></circle>
              </template>
              <text x="100" y="78" text-anchor="middle">{{ board.rotation }}°</text>
              <text x="100" y="115" text-anchor="middle">{{ mappingLabel(board) }}</text>
              <text v-if="spareLabel(board)" x="100" y="132" text-anchor="middle">{{ spareLabel(board) }}</text>
              <text x="100" y="160" text-anchor="middle">Near side ↓</text>
            </svg>
            <strong>{{ index + 1 }}. {{ board.name }}</strong>
          </button>
          <label :for="`controller-${board.id}`">WLED controller</label>
          <select :id="`controller-${board.id}`" :value="board.deviceMac ?? ''" :disabled="setupBusy" @change="assign(board, ($event.target as HTMLSelectElement).value)">
            <option value="">Assign later</option><option v-for="device in choices(board)" :key="device.mac" :value="device.mac">{{ device.name }} ({{ device.address }})</option>
          </select>
          <p v-if="board.deviceMac && installation.data.value?.value.outputErrors[board.deviceMac]" class="device-error" role="status">{{ installation.data.value.value.outputErrors[board.deviceMac] }}</p>
        </article>
      </div>
      <p v-if="legendStrips && !sessionId" class="strip-legend"><span v-for="strip in legendStrips" :key="strip"><span class="strip-swatch" :style="{ background: stripColors[(strip - 1) % 4] }"></span>Strip {{ strip }}</span><span><span class="strip-swatch strip-swatch-start"></span>First LED of a strip</span></p>
      <section v-if="sessionId && editing" class="calibration-panel" aria-label="Board calibration">
        <h2>Check {{ selectedPosition + 1 }}. {{ editing.name }}</h2>
        <p>Stand on the near side shown below. Check the lit board’s position, then rotate until its edge colors match the screen. The white marker travels clockwise.</p>
        <div class="calibration-guide" role="img" aria-label="Top blue, right green, bottom amber nearest you, left pink"><span class="guide-top">Top · Blue</span><span class="guide-left">Left<br />Pink</span><span class="guide-center">Clockwise ↻</span><span class="guide-right">Right<br />Green</span><span class="guide-bottom">Near side · Amber</span></div>
        <div class="button-group"><button :disabled="busy || selectedPosition === 0" @click="move(selectedPosition, -1)">Move board left</button><button :disabled="busy || selectedPosition === draft.boards.length - 1" @click="move(selectedPosition, 1)">Move board right</button><button :disabled="busy" @click="rotate(-90)">Rotate left</button><button :disabled="busy" @click="rotate(90)">Rotate right</button></div>
        <div class="button-group"><button :disabled="busy || step <= 0" @click="next(-1)">Previous board</button><span>{{ step + 1 }} of {{ assigned.length }} assigned boards</span><button :disabled="busy || step === assigned.length - 1" @click="next(1)">Next board</button></div>
        <div class="button-group"><button class="primary" :disabled="busy" @click="mutation.mutate('finish')">Finish and save</button><button :disabled="!connected || mutation.isPending.value" @click="mutation.mutate('cancel')">Cancel calibration</button></div>
      </section>
      <section v-if="editing" class="settings-section" aria-label="Board settings">
        <h2>{{ editing.name }}</h2>
        <div class="text-field"><label for="board-name">Name</label><input id="board-name" v-model="editing.name" :disabled="setupBusy" /></div>
        <!-- One set of controls for the selected board: it stays selected as it moves, so repeated clicks walk it along the row. -->
        <div v-if="!sessionId" class="button-group"><button :disabled="busy || selectedPosition === 0" @click="move(selectedPosition, -1)">← Move board left</button><button :disabled="busy || selectedPosition === draft.boards.length - 1" @click="move(selectedPosition, 1)">Move board right →</button><button class="danger" :disabled="setupBusy" @click="remove(editing.id)">Remove board</button></div>
        <p v-if="!sessionId" class="rotation-status">Rotation <strong>{{ editing.rotation }}°</strong></p>
        <div v-if="!sessionId" class="button-group rotation-controls"><button :disabled="busy" @click="rotate(-90)">↺ Rotate left</button><button :disabled="busy" @click="rotate(90)">Rotate right ↻</button><button :disabled="busy || !editing.deviceMac" @click="mutation.mutate('locate')">Locate board</button></div>
      </section>
      <details class="settings-section layout-advanced" :open="advanced" @toggle="advanced = ($event.target as HTMLDetailsElement).open">
        <summary>Advanced settings</summary>
        <section v-if="editing" class="settings-section" aria-label="LED mapping">
          <h2>LED mapping · {{ editing.name }}</h2>
          <p>Both strips start at one corner and run in opposite directions. Each covers two edges. Rotation turns this mapping clockwise.</p>
          <div class="parameters"><div><label for="rotation">Rotation</label><select id="rotation" v-model.number="editing.rotation" :disabled="busy" @change="preview"><option v-for="angle in [0, 90, 180, 270]" :key="angle" :value="angle">{{ angle }}°</option></select></div></div>
          <div class="button-group"><button :disabled="busy || editing.edges.length !== 4" @click="swap">Swap strips</button><button :disabled="setupBusy || !editing.deviceMac" @click="defaultMapping">Use two-strip template</button><button v-if="!editing.edges.length" :disabled="setupBusy || !editing.deviceMac" @click="manualMapping">Set mapping manually</button></div>
          <div class="edge-editors"><fieldset v-for="(edge, side) in editing.edges" :key="side" :disabled="setupBusy"><legend>{{ edgeNames[side] }} edge</legend><label :for="`start-${side}`">First LED index</label><input :id="`start-${side}`" v-model.number="edge.start" type="number" inputmode="numeric" min="0" /><label :for="`count-${side}`">LED count</label><input :id="`count-${side}`" v-model.number="edge.count" type="number" inputmode="numeric" min="1" /><label class="checkbox-label"><input v-model="edge.reversed" type="checkbox" />Reverse direction</label></fieldset></div>
        </section>
        <fieldset class="settings-section" :disabled="setupBusy"><legend>Idle sync</legend>
          <label class="checkbox-label"><input v-model="draft.idleSync.enabled" type="checkbox" />Enable WLED idle sync</label>
          <div class="parameters"><div><label for="idle-master">Master</label><select id="idle-master" v-model="draft.idleSync.masterMac"><option :value="null">Choose master</option><option v-for="device in draft.devices.filter(d => d.selected)" :key="device.mac" :value="device.mac">{{ device.name }}</option></select></div><div><label for="sync-group">Sync group</label><input id="sync-group" v-model.number="draft.idleSync.group" type="number" min="1" max="8" /></div></div>
          <div class="follower-list"><label v-for="device in draft.devices.filter(d => d.selected && d.mac !== draft!.idleSync.masterMac)" :key="device.mac" class="checkbox-label"><input v-model="draft.idleSync.followers" type="checkbox" :value="device.mac" />{{ device.name }}</label></div>
        </fieldset>
      </details>
      <div v-if="!sessionId" class="button-group layout-save"><button class="primary" :disabled="busy" @click="mutation.mutate('save')">Save layout</button><button :disabled="setupBusy" @click="reload">Reload saved settings</button></div>
    </template>
  </section>
</template>
