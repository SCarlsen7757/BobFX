<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import { useMutation } from '@tanstack/vue-query';
import { ArrowLeft, ArrowLeftRight, ArrowRight, ArrowUpDown, Check, ChevronLeft, ChevronRight, ListRestart, LocateFixed, MoveUp, Plus, RefreshCw, ScanEye, Trash2, X } from '@lucide/vue';
import { api } from '../lib/api';
import IconButton from '../components/IconButton.vue';
import LiveBoards from '../components/LiveBoards.vue';
import SaveBar from '../components/SaveBar.vue';
import { useUnsavedGuard } from '../lib/unsaved';
import { boardDots, boardTile } from '../lib/boardDrawing';
import { command, connected, synchronize, useInstallation } from '../lib/session';
import { boardProblems, clone, describeProblem, idleSyncProblems, ledCountNotice, newId, resetMapping, reverseStrip, sides, stripRange,
  swapSides, turnBoard, unmappedLeds, useCanConfigure, type Side } from '../lib/installation';
import type { Board, CalibrationSession, IdleSync, SavedDevice } from '../lib/contracts';

const installation = useInstallation();
const canConfigure = useCanConfigure();
// Only what this page saves is a draft. Controllers are read live, so one registered or rescanned while the page
// is open is offered at once and the draft is checked against its current LED count.
const draft = ref<{ boards: Board[]; idleSync: IdleSync }>();
const devices = computed<SavedDevice[]>(() => installation.data.value?.value.settings.devices ?? []);
const selectedId = ref('');
const boardRow = ref<HTMLElement>();
const editing = computed(() => draft.value?.boards.find(b => b.id === selectedId.value));
const editingDevice = computed(() => devices.value.find(d => d.mac === editing.value?.deviceMac));
const assigned = computed(() => draft.value?.boards.filter(b => b.deviceMac !== null) ?? []);
const selectedPosition = computed(() => draft.value?.boards.findIndex(b => b.id === selectedId.value) ?? -1);
const step = computed(() => assigned.value.findIndex(b => b.id === selectedId.value));
const sessionId = ref<string>();
const message = ref('');
const sideNames: Record<Side, string> = { left: 'Left', right: 'Right' };
const stripColors: Record<Side, string> = { left: '#ffad66', right: '#b18cff' };
// On screen the calibration colors are brighter than the dim blue and amber the LEDs are sent.
const checkColors: Record<Side, string> = { left: '#3a9ee6', right: '#e6a03a' };
let beforeCalibration: { boards: Board[]; idleSync: IdleSync } | undefined;
let heartbeat: ReturnType<typeof setInterval> | undefined;
let left = false;
let previewQueue: Promise<void> = Promise.resolve();

const problems = computed(() => draft.value ? boardProblems(draft.value.boards, devices.value) : []);
const syncProblems = computed(() => draft.value ? idleSyncProblems(draft.value.idleSync, devices.value) : []);
const blocking = computed(() => [...problems.value.map(problem => describeProblem(problem, draft.value?.boards ?? [])), ...syncProblems.value]);
function boardProblem(board: Board) { return problems.value.find(problem => problem.boardId === board.id)?.message; }
function fieldProblem(side: Side, part: 'start' | 'count') {
  return problems.value.find(problem => problem.boardId === selectedId.value && problem.side === side && problem.part === part)?.message;
}
/** Problems of the selected board that belong to both strips, such as two strips sharing LEDs. */
const mappingProblems = computed(() => problems.value.filter(problem => problem.boardId === selectedId.value && !problem.part && (problem.side || problem.bothStrips)));
const dirty = computed(() => {
  const saved = installation.data.value?.value.settings;
  return !!draft.value && !!saved && JSON.stringify(draft.value) !== JSON.stringify({ boards: saved.boards, idleSync: saved.idleSync });
});
function mappingLabel(board: Board, side: Side) { return `${sideNames[side]} ${stripRange(board[side]).replace('LEDs ', '').replace('LED ', '')}`; }
/** The board's place in the row and its name, cut to what fits between the two strips of its drawing. */
function tileName(board: Board, index: number) {
  const label = `${index + 1}. ${board.name}`;
  return label.length > 13 ? `${label.slice(0, 12)}…` : label;
}
function spareLabel(board: Board) {
  const device = devices.value.find(d => d.mac === board.deviceMac);
  const spare = device && board.left && board.right ? unmappedLeds(board, device.ledCount) : 0;
  return spare === 0 ? '' : spare === 1 ? '1 LED unused' : `${spare} LEDs unused`;
}
/** A board's strip colors: the calibration colors while it is the board being checked. */
function tileColors(board: Board) { return sessionId.value && selectedId.value === board.id ? checkColors : stripColors; }
function controllerLabel(device: SavedDevice) { return device.simulated ? `${device.name} (simulated)` : `${device.name} (${device.address})`; }

function reload() {
  const saved = installation.data.value?.value.settings;
  if (!saved || sessionId.value) return;
  draft.value = clone({ boards: saved.boards, idleSync: saved.idleSync });
  selectedId.value = draft.value.boards[0]?.id ?? ''; message.value = ''; mutation.reset();
}
function clearSession() { clearInterval(heartbeat); heartbeat = undefined; sessionId.value = undefined; }
function select(board: Board) {
  if (sessionId.value && !board.deviceMac) return;
  selectedId.value = board.id; preview();
}
function nextName() {
  let number = (draft.value?.boards.length ?? 0) + 1;
  while (draft.value?.boards.some(b => b.name === `Board ${number}`)) number++;
  return `Board ${number}`;
}
function add() {
  if (!draft.value) return;
  const board: Board = { id: newId(), name: nextName(), deviceMac: null, left: null, right: null };
  draft.value.boards.push(board); selectedId.value = board.id;
}
function choices(board: Board) {
  return devices.value.filter(d => d.selected && !draft.value?.boards.some(b => b.id !== board.id && b.deviceMac === d.mac));
}
function assign(board: Board, value: string) {
  const device = devices.value.find(d => d.mac === value);
  board.deviceMac = device?.mac ?? null;
  message.value = '';
  if (!device) return;
  // Keep a mapping that still fits this controller; otherwise start from the standard one.
  const fits = sides.every(side => { const strip = board[side]; return !!strip && strip.start >= 0 && strip.count >= 1 && strip.start + strip.count <= device.ledCount; });
  if (!fits) resetMapping(board, device.ledCount);
  message.value = ledCountNotice(device);
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
function change(action: (board: Board) => void) { if (editing.value) { action(editing.value); preview(); } }
function defaultMapping() {
  if (!editing.value || !editingDevice.value) return;
  resetMapping(editing.value, editingDevice.value.ledCount);
  message.value = ledCountNotice(editingDevice.value); preview();
}
function next(delta: number) { const board = assigned.value[step.value + delta]; if (board) select(board); }
function previewRequest() {
  if (!draft.value || !editing.value) throw new Error('Choose a board first.');
  // Only the boards are checked here: idle sync does not matter until the layout is saved.
  if (problems.value.length) throw new Error(describeProblem(problems.value[0]!, draft.value.boards));
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
  void sendPreview().catch(error => { message.value = `The boards could not be updated: ${(error as Error).message}`; void synchronize(); });
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
  if (blocking.value.length) throw new Error(blocking.value[0]);
  const idleSync = { ...draft.value.idleSync, followers: draft.value.idleSync.followers.filter(mac => mac !== draft.value!.idleSync.masterMac) };
  await command('/api/installation/layout', { boards: draft.value.boards, idleSync }, 'PUT');
  // Continue from exactly what was saved, so the page reads as saved whatever the server tidied up.
  const saved = installation.data.value?.value.settings;
  if (saved) draft.value = clone({ boards: saved.boards, idleSync: saved.idleSync });
  message.value = Object.keys(installation.data.value?.value.outputErrors ?? {}).length ? 'Layout saved; check device errors.' : 'Layout saved.';
}
const mutation = useMutation({ mutationFn: async (action: 'save' | 'start' | 'finish' | 'cancel' | 'locate' | 'simulated') => {
  message.value = '';
  if (action === 'save') await save();
  else if (action === 'locate') { await command(`/api/installation/devices/${editing.value?.deviceMac}/locate`); message.value = 'Locate finished.'; }
  else if (action === 'simulated') {
    if (!connected.value || !draft.value) throw new Error('Reconnect before adding a board.');
    // The controller is registered at once; the board that uses it is part of the draft until the layout is saved.
    const device = await api<SavedDevice>('/api/installation/devices/simulated', 'POST');
    await synchronize();
    const board: Board = { id: newId(), name: nextName(), deviceMac: device.mac, left: null, right: null };
    resetMapping(board, device.ledCount);
    draft.value.boards.push(board); selectedId.value = board.id;
  } else if (action === 'start') {
    if (!editing.value?.deviceMac) selectedId.value = assigned.value[0]?.id ?? '';
    const request = previewRequest();
    beforeCalibration = clone(draft.value!);
    const session = await calibrationCommand<CalibrationSession>('/api/installation/calibration', 'POST', request);
    // The page may have been left while the check was starting; nothing would stop that check or its heartbeat.
    if (left) { release(session.sessionId); return; }
    sessionId.value = session.sessionId;
    heartbeat = setInterval(preview, 5000);
    await synchronize();
  } else {
    await stopCalibration();
    if (action === 'finish') await save();
    else { if (beforeCalibration) draft.value = clone(beforeCalibration); message.value = 'Check cancelled.'; }
  }
} });
const busy = computed(() => !connected.value || mutation.isPending.value || (!canConfigure.value && !sessionId.value));
const setupBusy = computed(() => busy.value || !!sessionId.value);
// A failed save must not keep its banner once the draft has been changed.
watch(draft, () => { if (mutation.error.value && !mutation.isPending.value) mutation.reset(); }, { deep: true });
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
    clearSession(); message.value = 'The check ended.';
  }
}, { immediate: true });

// Asked first: staying on the page keeps a running check going.
useUnsavedGuard(dirty);
onBeforeRouteLeave(async () => {
  try { await stopCalibration(); }
  catch { clearSession(); /* The server lease releases output if navigation loses the connection. */ }
});
/** Ends a check without waiting for the answer, so it also works while the page is going away. */
function release(id: string) {
  void fetch(`/api/installation/calibration/${id}`, { method: 'DELETE', credentials: 'same-origin', keepalive: true }).catch(() => {});
}
function leaving() {
  const id = sessionId.value;
  clearSession();
  if (id) release(id);
}
window.addEventListener('pagehide', leaving);
onBeforeUnmount(() => { left = true; window.removeEventListener('pagehide', leaving); leaving(); });
</script>

<template>
  <section class="settings-page layout-page">
    <h1 tabindex="-1">Board layout</h1>
    <p v-if="message || mutation.error.value" role="status" :class="{ 'error-message': mutation.error.value }">{{ mutation.error.value?.message ?? message }}</p>
    <p v-if="installation.data.value?.value.calibration && !sessionId" role="status">Boards are being checked in another window.</p>
    <template v-if="draft">
      <div class="layout-toolbar button-group">
        <button class="primary" :disabled="setupBusy" aria-label="Add board" title="Add board" @click="add"><Plus />Board</button>
        <button :disabled="setupBusy" aria-label="Add simulated board" title="Add simulated board" @click="mutation.mutate('simulated')"><Plus />Simulated</button>
        <button :disabled="setupBusy || !assigned.length" aria-label="Check on the boards" title="Check on the boards" @click="mutation.mutate('start')"><ScanEye />Check</button>
      </div>
      <div ref="boardRow" class="board-row" aria-label="Boards from left to right" tabindex="0">
        <article v-for="(board, index) in draft.boards" :key="board.id" :data-board-id="board.id" class="board-item" :class="{ 'unassigned-board': !board.deviceMac }">
          <button class="board-tile" :class="{ selected: selectedId === board.id }" :aria-pressed="selectedId === board.id" :disabled="busy || (!!sessionId && !board.deviceMac)" @click="select(board)">
            <svg :viewBox="`0 0 ${boardTile.width} ${boardTile.bottom + 12}`" role="img" :aria-label="`${board.name}: ${mappingLabel(board, 'left')}, ${mappingLabel(board, 'right')}`">
              <rect :x="boardTile.left" :y="boardTile.top" :width="boardTile.right - boardTile.left" :height="boardTile.bottom - boardTile.top" rx="3" class="board-surface" stroke-width="3" />
              <template v-for="dot in boardDots(board)" :key="`${dot.side}-${dot.index}`">
                <circle v-if="dot.first" class="strip-start" :cx="dot.x" :cy="dot.y" r="6" fill="none" :stroke="tileColors(board)[sides[dot.side]!]" stroke-width="1.5" />
                <circle :cx="dot.x" :cy="dot.y" r="2" :fill="tileColors(board)[sides[dot.side]!]"><title>{{ sideNames[sides[dot.side]!] }} strip · LED {{ dot.index }}{{ dot.first ? ' (first LED)' : '' }}</title></circle>
              </template>
              <text class="board-name" :x="boardTile.width / 2" y="108" text-anchor="middle">{{ tileName(board, index) }}</text>
              <template v-if="board.left || board.right">
                <!-- The strip's own color on its range stands in for a legend. -->
                <text :x="boardTile.width / 2" y="134" text-anchor="middle" :style="{ fill: tileColors(board).left }">{{ mappingLabel(board, 'left') }}</text>
                <text :x="boardTile.width / 2" y="152" text-anchor="middle" :style="{ fill: tileColors(board).right }">{{ mappingLabel(board, 'right') }}</text>
                <text v-if="spareLabel(board)" :x="boardTile.width / 2" y="170" text-anchor="middle">{{ spareLabel(board) }}</text>
              </template>
            </svg>
          </button>
          <select :id="`controller-${board.id}`" aria-label="WLED controller" title="WLED controller" :value="board.deviceMac ?? ''" :disabled="setupBusy" @change="assign(board, ($event.target as HTMLSelectElement).value)">
            <option value="">No controller</option><option v-for="device in choices(board)" :key="device.mac" :value="device.mac">{{ controllerLabel(device) }}</option>
          </select>
          <p v-if="boardProblem(board)" class="device-error" role="status">{{ boardProblem(board) }}</p>
          <p v-else-if="board.deviceMac && installation.data.value?.value.outputErrors[board.deviceMac]" class="device-error" role="status">{{ installation.data.value.value.outputErrors[board.deviceMac] }}</p>
        </article>
      </div>

      <section v-if="sessionId && editing" class="calibration-panel" aria-label="Check on the boards">
        <h2>Check {{ selectedPosition + 1 }}. {{ editing.name }}</h2>
        <!-- What a correctly mapped board shows, as a key instead of a paragraph. -->
        <p class="check-key" title="The left strip is blue, the right strip amber, and the white dots run away from you">
          <span><span class="strip-swatch" :style="{ background: checkColors.left }"></span>Left</span>
          <span><span class="strip-swatch" :style="{ background: checkColors.right }"></span>Right</span>
          <span aria-label="The white dots run away from you"><span class="strip-swatch check-dot"></span><MoveUp /></span>
        </p>
        <LiveBoards :boards="draft.boards" :active-id="editing.id" />
        <div class="button-group">
          <IconButton :icon="ChevronLeft" label="Previous board" :disabled="busy || step <= 0" @click="next(-1)" />
          <span class="check-step">{{ step + 1 }} / {{ assigned.length }}</span>
          <IconButton :icon="ChevronRight" label="Next board" :disabled="busy || step === assigned.length - 1" @click="next(1)" />
          <button class="primary" :disabled="busy" aria-label="Finish and save" title="Finish and save" @click="mutation.mutate('finish')"><Check />Save</button>
          <IconButton :icon="X" label="Cancel check" :disabled="!connected || mutation.isPending.value" @click="mutation.mutate('cancel')" />
        </div>
      </section>

      <!-- One set of controls for the selected board, in and out of a check: it stays selected as it moves, so repeated clicks walk it along the row. -->
      <section v-if="editing" class="settings-section" aria-label="Board settings">
        <div class="board-bar">
          <input id="board-name" v-model="editing.name" aria-label="Name" title="Name" :disabled="setupBusy" />
          <IconButton :icon="ArrowLeft" label="Move board left" :disabled="busy || selectedPosition === 0" @click="move(selectedPosition, -1)" />
          <IconButton :icon="ArrowRight" label="Move board right" :disabled="busy || selectedPosition === draft.boards.length - 1" @click="move(selectedPosition, 1)" />
          <IconButton :icon="LocateFixed" label="Locate board" :disabled="setupBusy || !editing.deviceMac" @click="mutation.mutate('locate')" />
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
        <section v-if="editing && editing.left && editing.right" class="settings-section" aria-label="LED numbers">
          <h2>LED numbers · {{ editing.name }}</h2>
          <p v-if="editingDevice" class="preview-note">{{ editingDevice.name }} · LEDs 0–{{ editingDevice.ledCount - 1 }}</p>
          <div class="strip-editors">
            <fieldset v-for="side in sides" :key="side" :disabled="setupBusy"><legend :style="{ color: stripColors[side] }">{{ sideNames[side] }} strip</legend>
              <label :for="`start-${side}`">First LED</label><input :id="`start-${side}`" v-model.number="editing[side]!.start" type="number" inputmode="numeric" min="0" :aria-invalid="!!fieldProblem(side, 'start')" :aria-describedby="`start-${side}-problem`" @change="preview" />
              <p v-if="fieldProblem(side, 'start')" :id="`start-${side}-problem`" class="field-problem" role="status">{{ fieldProblem(side, 'start') }}</p>
              <label :for="`count-${side}`">LED count</label><input :id="`count-${side}`" v-model.number="editing[side]!.count" type="number" inputmode="numeric" min="1" :aria-invalid="!!fieldProblem(side, 'count')" :aria-describedby="`count-${side}-problem`" @change="preview" />
              <p v-if="fieldProblem(side, 'count')" :id="`count-${side}-problem`" class="field-problem" role="status">{{ fieldProblem(side, 'count') }}</p>
              <label class="checkbox-label"><input v-model="editing[side]!.reversed" type="checkbox" @change="preview" />Reversed</label>
            </fieldset>
          </div>
          <p v-for="problem in mappingProblems" :key="problem.message" class="field-problem mapping-problem" role="status">{{ problem.message }}</p>
        </section>
        <fieldset class="settings-section" :disabled="setupBusy"><legend>Idle sync</legend>
          <label class="checkbox-label"><input v-model="draft.idleSync.enabled" type="checkbox" />Enable WLED idle sync</label>
          <div class="parameters"><div><label for="idle-master">Master</label><select id="idle-master" v-model="draft.idleSync.masterMac"><option :value="null">None</option><option v-for="device in devices.filter(d => d.selected)" :key="device.mac" :value="device.mac">{{ device.name }}</option></select></div><div><label for="sync-group">Sync group</label><input id="sync-group" v-model.number="draft.idleSync.group" type="number" min="1" max="8" /></div></div>
          <div class="follower-list"><label v-for="device in devices.filter(d => d.selected && d.mac !== draft!.idleSync.masterMac)" :key="device.mac" class="checkbox-label"><input v-model="draft.idleSync.followers" type="checkbox" :value="device.mac" />{{ device.name }}</label></div>
          <p v-for="problem in syncProblems" :key="problem" class="field-problem" role="status">{{ problem }}</p>
        </fieldset>
      </details>

      <SaveBar v-if="!sessionId" :dirty="dirty" label="Save layout" :disabled="busy || blocking.length > 0" reload-label="Reload saved settings" :reload-disabled="setupBusy"
        :note="mutation.error.value?.message ?? message" :failed="!!mutation.error.value" @save="mutation.mutate('save')" @reload="reload">
        <ul v-if="blocking.length" class="save-problems" role="status"><li v-for="problem in blocking" :key="problem">{{ problem }}</li></ul>
      </SaveBar>
    </template>
  </section>
</template>
