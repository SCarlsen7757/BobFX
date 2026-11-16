<script setup lang="ts">
import { computed } from 'vue';
import type { IdleSync, SavedDevice } from '../lib/contracts';

// WLED's own sync between controllers while no game is running: one master and the controllers that follow it.
// It edits the page's draft in place.
const props = defineProps<{ idleSync: IdleSync; devices: SavedDevice[]; problems: string[]; disabled: boolean }>();
const selectable = computed(() => props.devices.filter(device => device.selected));
const followers = computed(() => selectable.value.filter(device => device.mac !== props.idleSync.masterMac));
</script>

<template>
  <fieldset class="settings-section" :disabled="disabled"><legend>Idle sync</legend>
    <label class="checkbox-label"><input v-model="idleSync.enabled" type="checkbox" />Enable WLED idle sync</label>
    <div class="parameters">
      <div><label for="idle-master">Master</label><select id="idle-master" v-model="idleSync.masterMac"><option :value="null">None</option><option v-for="device in selectable" :key="device.mac" :value="device.mac">{{ device.name }}</option></select></div>
      <div><label for="sync-group">Sync group</label><input id="sync-group" v-model.number="idleSync.group" type="number" min="1" max="8" /></div>
    </div>
    <div class="follower-list"><label v-for="device in followers" :key="device.mac" class="checkbox-label"><input v-model="idleSync.followers" type="checkbox" :value="device.mac" />{{ device.name }}</label></div>
    <p v-for="problem in problems" :key="problem" class="field-problem" role="status">{{ problem }}</p>
  </fieldset>
</template>
