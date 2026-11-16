<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import { ArrowLeft, ArrowLeftRight, ArrowRight, ArrowUpDown, ListRestart, LocateFixed, Plus, RefreshCw, ScanEye, Trash2 } from '@lucide/vue';
import { api } from '../lib/api';
import BoardTile from '../components/BoardTile.vue';
import CalibrationPanel from '../components/CalibrationPanel.vue';
import IconButton from '../components/IconButton.vue';
import IdleSyncEditor from '../components/IdleSyncEditor.vue';
import SaveBar from '../components/SaveBar.vue';
import StatusMessage from '../components/StatusMessage.vue';
import StripEditor from '../components/StripEditor.vue';
import { useCalibrationSession } from '../lib/calibration';
import { useSectionDraft } from '../lib/draft';
import { endpoints } from '../lib/endpoints';
import { command, connected, limits, synchronize } from '../lib/session';
import { boardProblems, clone, describeProblem, fitsController, idleSyncProblems, ledCountNotice, newId, resetMapping, reverseStrip,
  swapSides, turnBoard, useCanConfigure, useInstallationState } from '../lib/installation';
import { usePageStatus } from '../lib/status';
import type { Board, SavedDevice } from '../lib/contracts';

// Only what this page saves is a draft. Controllers are read live, so one registered or rescanned while the page
// is open is offered at once and the draft is checked against its current LED count.
const { devices, outputErrors, calibration } = useInstallationState();
const canConfigure = useCanConfigure();
// Asked first: staying on the page keeps a running check going.
const { draft, dirty, adopt } = useSectionDraft(settings => ({ boards: settings.boards, idleSync: settings.idleSync }));
const status = usePageStatus();
const selectedId = ref('');
const boardRow = ref<HTMLElement>();
const editing = computed(() => draft.value?.boards.find(b => b.id === selectedId.value));
const editingDevice = computed(() => devices.value.find(d => d.mac === editing.value?.deviceMac));
const assigned = computed(() => draft.value?.boards.filter(b => b.deviceMac !== null) ?? []);
const selectedPosition = computed(() => draft.value?.boards.findIndex(b => b.id === selectedId.value) ?? -1);
const step = computed(() => assigned.value.findIndex(b => b.id === selectedId.value));
function selectFirst() { selectedId.value = draft.value?.boards[0]?.id ?? ''; }
watch(() => !!draft.value, loaded => { if (loaded) selectFirst(); }, { immediate: true });

const problems = computed(() => draft.value ? boardProblems(draft.value.boards, devices.value) : []);
const syncProblems = computed(() => draft.value ? idleSyncProblems(draft.value.idleSync, devices.value, limits.value?.syncGroup) : []);
const blocking = computed(() => [...problems.value.map(problem => describeProblem(problem, draft.value?.boards ?? [])), ...syncProblems.value]);
// Everything the row shows about a board that takes looking something up, worked out once per change.
const tiles = computed(() => (draft.value?.boards ?? []).map((board, position) => ({
  board, position, device: devices.value.find(d => d.mac === board.deviceMac),
  choices: devices.value.filter(d => d.selected && !draft.value?.boards.some(b => b.id !== board.id && b.deviceMac === d.mac)),
  problem: problems.value.find(problem => problem.boardId === board.id)?.message
    ?? (board.deviceMac ? outputErrors.value[board.deviceMac] : undefined),
})));
function controllerLabel(device: SavedDevice) { return device.simulated ? `${device.name} (simulated)` : `${device.name} (${device.address})`; }

function previewRequest() {
  if (!draft.value || !editing.value) throw new Error('Choose a board first.');
  // Only the boards are checked here: idle sync does not matter until the layout is saved.
  const problem = problems.value[0];
  if (problem) throw new Error(describeProblem(problem, draft.value.boards));
  return { boards: draft.value.boards, activeBoardId: editing.value.id };
}
const check = useCalibrationSession(previewRequest, message => { status.message.value = message; }, () => status.busy.value);
const sessionId = check.sessionId;
// What the draft was before a check, which a cancelled check goes back to.
let beforeCheck: typeof draft.value;

function reload() {
  if (sessionId.value) return;
  adopt(); selectFirst(); status.clear();
}
function select(board: Board) {
  if (sessionId.value && !board.deviceMac) return;
  selectedId.value = board.id; check.update();
}
function nextName() {
  let number = (draft.value?.boards.length ?? 0) + 1;
  while (draft.value?.boards.some(b => b.name === `Board ${number}`)) number++;
  return `Board ${number}`;
}
function addBoard(device?: SavedDevice) {
  if (!draft.value) return;
  const board: Board = { id: newId(), name: nextName(), deviceMac: device?.mac ?? null, left: null, right: null };
  if (device) resetMapping(board, device.ledCount);
  draft.value.boards.push(board); selectedId.value = board.id;
}
function assign(board: Board, value: string) {
  const device = devices.value.find(d => d.mac === value);
  board.deviceMac = device?.mac ?? null;
  status.message.value = '';
  if (!device) return;
  // Keep a mapping that still fits this controller; otherwise start from the standard one.
  if (!fitsController(board, device.ledCount)) resetMapping(board, device.ledCount);
  status.message.value = ledCountNotice(device);
}
function move(index: number, delta: number) {
  const boards = draft.value?.boards, board = boards?.[index], other = boards?.[index + delta];
  if (!boards || !board || !other) return;
  boards[index] = other; boards[index + delta] = board; check.update();
}
function remove(id: string) {
  if (!draft.value) return;
  draft.value.boards = draft.value.boards.filter(b => b.id !== id);
  if (selectedId.value === id) selectFirst();
}
function change(action: (board: Board) => void) { if (editing.value) { action(editing.value); check.update(); } }
function defaultMapping() {
  if (!editing.value || !editingDevice.value) return;
  resetMapping(editing.value, editingDevice.value.ledCount);
  status.message.value = ledCountNotice(editingDevice.value); check.update();
}
function next(delta: number) { const board = assigned.value[step.value + delta]; if (board) select(board); }

async function saveLayout() {
  if (!draft.value) return '';
  if (blocking.value[0]) throw new Error(blocking.value[0]);
  const { boards, idleSync } = draft.value;
  await command(endpoints.installation.layout, { boards, idleSync: { ...idleSync, followers: idleSync.followers.filter(mac => mac !== idleSync.masterMac) } }, 'PUT');
  // Continue from exactly what was saved, so the page reads as saved whatever the server tidied up.
  adopt();
  return Object.keys(outputErrors.value).length ? 'Layout saved; check device errors.' : 'Layout saved.';
}
const save = status.action(saveLayout);
const locate = status.action(async () => {
  const mac = editing.value?.deviceMac;
  if (!mac) return;
  await command(endpoints.installation.deviceAction(mac, 'locate'));
  return 'Locate finished.';
});
const addSimulated = status.action(async () => {
  if (!connected.value || !draft.value) throw new Error('Reconnect before adding a board.');
  // The controller is registered at once; the board that uses it is part of the draft until the layout is saved.
  const device = await api<SavedDevice>(endpoints.installation.simulatedDevice, 'POST');
  await synchronize();
  addBoard(device);
});
const startCheck = status.action(async () => {
  if (!editing.value?.deviceMac) selectedId.value = assigned.value[0]?.id ?? '';
  previewRequest();
  beforeCheck = clone(draft.value);
  await check.start();
});
const finishCheck = status.action(async () => { await check.stop(); return saveLayout(); });
const cancelCheck = status.action(async () => {
  await check.stop();
  if (beforeCheck) draft.value = clone(beforeCheck);
  return 'Check cancelled.';
});

const busy = computed(() => !connected.value || status.busy.value || (!canConfigure.value && !sessionId.value));
const setupBusy = computed(() => busy.value || !!sessionId.value);
// A failed save must not keep its banner once the draft has been changed.
watch(draft, () => { if (status.error.value && !status.busy.value) status.error.value = ''; }, { deep: true });
watch([selectedId, selectedPosition], async () => {
  await nextTick();
  const row = boardRow.value;
  const card = Array.from(row?.querySelectorAll<HTMLElement>('.board-item') ?? []).find(item => item.dataset.boardId === selectedId.value);
  if (!row || !card) return;
  const container = row.getBoundingClientRect(), item = card.getBoundingClientRect();
  if (item.left < container.left) row.scrollLeft += item.left - container.left - 4;
  else if (item.right > container.right) row.scrollLeft += item.right - container.right + 4;
});
onBeforeRouteLeave(async () => {
  try { await check.stop(); }
  catch { check.clear(); /* The server lease releases output if navigation loses the connection. */ }
});
</script>

<template>
  <section class="settings-page layout-page">
    <h1 tabindex="-1">Board layout</h1>
    <StatusMessage :status="status" />
    <p v-if="calibration && !sessionId" role="status">Boards are being checked in another window.</p>
    <template v-if="draft">
      <div class="layout-toolbar button-group">
        <button class="primary" :disabled="setupBusy" aria-label="Add board" title="Add board" @click="addBoard()"><Plus />Board</button>
        <button :disabled="setupBusy" aria-label="Add simulated board" title="Add simulated board" @click="addSimulated"><Plus />Simulated</button>
        <button :disabled="setupBusy || !assigned.length" aria-label="Check on the boards" title="Check on the boards" @click="startCheck"><ScanEye />Check</button>
      </div>
      <div ref="boardRow" class="board-row" role="group" aria-label="Boards from left to right" tabindex="0">
        <article v-for="tile in tiles" :key="tile.board.id" :data-board-id="tile.board.id" class="board-item" :class="{ 'unassigned-board': !tile.board.deviceMac }">
          <BoardTile :board="tile.board" :position="tile.position" :device="tile.device" :selected="selectedId === tile.board.id" :checking="!!sessionId && selectedId === tile.board.id"
            :disabled="busy || (!!sessionId && !tile.board.deviceMac)" @click="select(tile.board)" />
          <select :id="`controller-${tile.board.id}`" :aria-label="`WLED controller for ${tile.board.name}`" title="WLED controller" :value="tile.board.deviceMac ?? ''" :disabled="setupBusy" @change="assign(tile.board, ($event.target as HTMLSelectElement).value)">
            <option value="">No controller</option><option v-for="device in tile.choices" :key="device.mac" :value="device.mac">{{ controllerLabel(device) }}</option>
          </select>
          <p v-if="tile.problem" class="device-error" role="status">{{ tile.problem }}</p>
        </article>
      </div>

      <CalibrationPanel v-if="sessionId && editing" :boards="draft.boards" :board="editing" :position="selectedPosition" :step="step" :steps="assigned.length"
        :busy="busy" :can-cancel="connected && !status.busy.value" @next="next" @finish="finishCheck" @cancel="cancelCheck" />

      <!-- One set of controls for the selected board, in and out of a check: it stays selected as it moves, so repeated clicks walk it along the row. -->
      <section v-if="editing" class="settings-section" aria-label="Board settings">
        <div class="board-bar">
          <input id="board-name" v-model="editing.name" aria-label="Name" title="Name" :disabled="setupBusy" />
          <IconButton :icon="ArrowLeft" label="Move board left" :disabled="busy || selectedPosition === 0" @click="move(selectedPosition, -1)" />
          <IconButton :icon="ArrowRight" label="Move board right" :disabled="busy || selectedPosition === draft.boards.length - 1" @click="move(selectedPosition, 1)" />
          <IconButton :icon="LocateFixed" label="Locate board" :disabled="setupBusy || !editing.deviceMac" @click="locate" />
          <IconButton class="danger" :icon="Trash2" label="Remove board" :disabled="setupBusy" @click="remove(editing.id)" />
        </div>
        <div v-if="editing.deviceMac" class="board-bar" role="group" aria-label="LED strips">
          <IconButton :icon="ArrowLeftRight" label="Swap left and right" :disabled="busy || !editing.left || !editing.right" @click="change(swapSides)" />
          <IconButton :icon="ArrowUpDown" label="Reverse left strip" :disabled="busy || !editing.left" @click="change(board => reverseStrip(board, 'left'))"><span aria-hidden="true">L</span></IconButton>
          <IconButton :icon="ArrowUpDown" label="Reverse right strip" :disabled="busy || !editing.right" @click="change(board => reverseStrip(board, 'right'))"><span aria-hidden="true">R</span></IconButton>
          <IconButton :icon="RefreshCw" label="Turn board 180°" :disabled="busy || !editing.left || !editing.right" @click="change(turnBoard)" />
          <IconButton :icon="ListRestart" label="Reset mapping" :disabled="setupBusy || !editingDevice" @click="defaultMapping" />
        </div>
      </section>

      <details class="settings-section layout-advanced">
        <summary>Advanced settings</summary>
        <StripEditor v-if="editing && editing.left && editing.right" :board="editing" :device="editingDevice" :problems="problems.filter(problem => problem.boardId === editing?.id)"
          :disabled="setupBusy" @changed="check.update" />
        <IdleSyncEditor :idle-sync="draft.idleSync" :devices="devices" :problems="syncProblems" :disabled="setupBusy" />
      </details>

      <SaveBar v-if="!sessionId" :dirty="dirty" label="Save layout" :disabled="busy || blocking.length > 0" reload-label="Reload saved settings" :reload-disabled="setupBusy"
        :note="status.text.value" :failed="status.failed.value" @save="save" @reload="reload">
        <ul v-if="blocking.length" class="save-problems" role="status"><li v-for="problem in blocking" :key="problem">{{ problem }}</li></ul>
      </SaveBar>
    </template>
  </section>
</template>
