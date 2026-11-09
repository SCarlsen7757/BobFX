<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useMutation } from '@tanstack/vue-query';
import { command, useInstallation } from '../lib/session';
import { boardPixels, clone, newId, useCanConfigure, validateBoards } from '../lib/installation';
import type { Board, InstallationSettings } from '../lib/contracts';
const installation = useInstallation();
const canConfigure = useCanConfigure();
const draft = ref<InstallationSettings>();
const editing = ref<Board>();
const newDevice = ref('');
const message = ref('');
const edgeNames = ['Top', 'Right', 'Bottom', 'Left'];
function reload() {
  if (!installation.data.value) return;
  draft.value = clone(installation.data.value.value.settings); editing.value = undefined; message.value = '';
}
watch(() => installation.data.value, () => { if (!draft.value) reload(); }, { immediate: true });
const available = computed(() => draft.value?.devices.filter(d => d.selected && !draft.value?.boards.some(b => b.deviceMac === d.mac)) ?? []);
function edit(board: Board) { editing.value = clone(board); }
function add() {
  const device = available.value.find(d => d.mac === newDevice.value);
  if (!device || !draft.value) return;
  if (device.ledCount < 4) { message.value = 'A square board needs at least four LEDs.'; return; }
  let start = 0;
  const board: Board = { id: newId(), name: device.name, deviceMac: device.mac, rotation: 0,
    edges: Array.from({ length: 4 }, (_, side) => {
      const count = Math.floor(device.ledCount / 4) + (side < device.ledCount % 4 ? 1 : 0);
      const edge = { start, count, reversed: false }; start += count; return edge;
    }) };
  draft.value.boards.push(board); newDevice.value = ''; edit(board);
}
function move(index: number, delta: number) {
  if (!draft.value) return;
  const boards = draft.value.boards;
  [boards[index], boards[index + delta]] = [boards[index + delta]!, boards[index]!];
}
function remove(id: string) {
  if (!draft.value) return;
  draft.value.boards = draft.value.boards.filter(b => b.id !== id);
  if (editing.value?.id === id) editing.value = undefined;
}
function apply() {
  if (!draft.value || !editing.value) return true;
  try {
    const boards = draft.value.boards.map(b => b.id === editing.value!.id ? clone(editing.value!) : b);
    validateBoards({ ...draft.value, boards });
    draft.value.boards = boards; message.value = 'Board settings applied.'; return true;
  } catch (error) { message.value = (error as Error).message; return false; }
}
function edgePreview(side: number) {
  const edge = editing.value?.edges[side];
  if (!edge || !Number.isInteger(edge.start) || !Number.isInteger(edge.count) || edge.start < 0 || edge.count < 1 || edge.count > 65535) return '';
  return Array.from({ length: Math.min(12, edge.count) }, (_, i) => edge.start + (edge.reversed ? edge.count - 1 - i : i)).join(' → ');
}
const mutation = useMutation({ mutationFn: async (action: 'save' | 'locate') => {
  message.value = '';
  if (action === 'locate') {
    await command(`/api/installation/devices/${editing.value?.deviceMac}/locate`); message.value = 'Locate finished.'; return;
  }
  if (!draft.value || !apply()) return;
  const idleSync = { ...draft.value.idleSync, followers: draft.value.idleSync.followers.filter(mac => mac !== draft.value!.idleSync.masterMac) };
  await command('/api/installation/layout', { boards: draft.value.boards, idleSync, animation: draft.value.animation }, 'PUT');
  message.value = Object.keys(installation.data.value?.value.outputErrors ?? {}).length ? 'Layout saved; check device errors.' : 'Layout saved.';
} });
const busy = computed(() => !canConfigure.value || mutation.isPending.value);
</script>

<template>
  <section class="settings-page layout-page">
    <h1 tabindex="-1">Board layout</h1>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
    <template v-if="draft">
      <div class="board-row">
        <div v-for="(board, index) in draft.boards" :key="board.id" class="board-item">
          <button class="board-tile" :class="{ selected: editing?.id === board.id }" :aria-pressed="editing?.id === board.id" @click="edit(board)">
            <svg viewBox="0 0 200 200" aria-label="LED strip mapping preview">
              <circle v-for="(pixel, pixelIndex) in boardPixels(board)" :key="pixelIndex" :cx="pixel.x" :cy="pixel.y" r="2.5" :fill="pixel.playerEdge ? '#96eddf' : '#ff4fad'"><title>LED {{ pixel.index }}</title></circle>
              <text x="100" y="82" text-anchor="middle">Top</text><text x="100" y="132" text-anchor="middle">Bottom</text>
            </svg>
            <strong>{{ index + 1 }}. {{ board.name }}</strong>
          </button>
          <div class="board-actions"><button :aria-label="`Move ${board.name} left`" :disabled="index === 0" @click="move(index, -1)">←</button><button :aria-label="`Move ${board.name} right`" :disabled="index === draft.boards.length - 1" @click="move(index, 1)">→</button><button :aria-label="`Remove ${board.name}`" @click="remove(board.id)">Remove</button></div>
        </div>
      </div>
      <div class="add-board"><label for="new-device">Device for new board</label><select id="new-device" v-model="newDevice"><option value="">Choose device</option><option v-for="device in available" :key="device.mac" :value="device.mac">{{ device.name }} ({{ device.ledCount }} LEDs)</option></select><button :disabled="!newDevice" @click="add">Add board</button></div>
      <section v-if="editing" class="settings-section" aria-label="Board settings">
        <h2>Board settings</h2>
        <div class="parameters">
          <div><label for="board-name">Name</label><input id="board-name" v-model="editing.name" /></div>
          <div><label for="board-device">WLED device</label><select id="board-device" v-model="editing.deviceMac"><option v-for="device in draft.devices.filter(d => d.selected)" :key="device.mac" :value="device.mac">{{ device.name }} ({{ device.ledCount }} LEDs)</option></select></div>
          <div><label for="rotation">Rotation</label><select id="rotation" v-model.number="editing.rotation"><option v-for="angle in [0, 90, 180, 270]" :key="angle" :value="angle">{{ angle }}°</option></select></div>
        </div>
        <div class="edge-editors">
          <fieldset v-for="(edge, side) in editing.edges" :key="side"><legend>{{ edgeNames[side] }} strip</legend>
            <label :for="`start-${side}`">First LED index</label><input :id="`start-${side}`" v-model.number="edge.start" type="number" inputmode="numeric" min="0" />
            <label :for="`count-${side}`">LED count</label><input :id="`count-${side}`" v-model.number="edge.count" type="number" inputmode="numeric" min="1" />
            <label class="checkbox-label"><input v-model="edge.reversed" type="checkbox" />Reverse direction</label>
            <p class="edge-indices">{{ edgePreview(side) }}</p>
          </fieldset>
        </div>
        <div class="button-group"><button @click="apply">Apply board settings</button><button :disabled="busy" @click="mutation.mutate('locate')">Locate board</button></div>
      </section>
      <section class="settings-section"><h2>Idle sync</h2>
        <label class="checkbox-label"><input v-model="draft.idleSync.enabled" type="checkbox" />Enable WLED idle sync</label>
        <div class="parameters"><div><label for="idle-master">Master</label><select id="idle-master" v-model="draft.idleSync.masterMac"><option :value="null">Choose master</option><option v-for="device in draft.devices.filter(d => d.selected)" :key="device.mac" :value="device.mac">{{ device.name }}</option></select></div><div><label for="sync-group">Sync group</label><input id="sync-group" v-model.number="draft.idleSync.group" type="number" min="1" max="8" /></div></div>
        <div class="follower-list"><label v-for="device in draft.devices.filter(d => d.selected && d.mac !== draft!.idleSync.masterMac)" :key="device.mac" class="checkbox-label"><input v-model="draft.idleSync.followers" type="checkbox" :value="device.mac" />{{ device.name }}</label></div>
      </section>
      <section class="settings-section"><h2>Animation timing</h2><div class="parameters">
        <div><label for="fps">Frames per second</label><input id="fps" v-model.number="draft.animation.framesPerSecond" type="number" min="10" max="60" /></div>
        <div><label for="flash-seconds">Red flash seconds</label><input id="flash-seconds" v-model.number="draft.animation.flashSeconds" type="number" min="1" max="10" step="0.1" /></div>
        <div><label for="dot-seconds">Dot seconds</label><input id="dot-seconds" v-model.number="draft.animation.dotSeconds" type="number" min="1" max="30" step="0.1" /></div>
        <div><label for="dot-speed">Dot circuits per second</label><input id="dot-speed" v-model.number="draft.animation.dotLapsPerSecond" type="number" min="1" max="20" step="0.1" /></div>
      </div></section>
      <div class="button-group"><button class="primary" :disabled="busy" @click="mutation.mutate('save')">Save layout and settings</button><button :disabled="mutation.isPending.value" @click="reload">Reload saved settings</button></div>
    </template>
  </section>
</template>
