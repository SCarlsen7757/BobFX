<script setup lang="ts">
import { computed } from 'vue';
import { Square } from '@lucide/vue';
import IconButton from './IconButton.vue';
import { useLightShowTest } from '../lib/lightShowTest';
import { segments } from '../lib/segments';
import { connected } from '../lib/session';
import type { PageStatus } from '../lib/status';
import type { AnimationSettings, LightShowSegment, LightShowSettings } from '../lib/contracts';

// The buttons that play a part of the light show on the real boards, and the line that says what is playing.
// `show` supplies the settings to play, so a page can test its unsaved draft; it may throw when they are not usable.
const props = defineProps<{
  status: PageStatus;
  show: () => { lightShow: LightShowSettings; animation: AnimationSettings };
  /** Added to "Playing …", where the page shows more than the boards. */
  where?: string;
  testid: string;
}>();
const emit = defineEmits<{ started: [] }>();
const { test, lightsFree, hasBoards, playing, unavailable, start, stop } = useLightShowTest();
const busy = computed(() => !connected.value || props.status.busy.value);
const play = props.status.action(async (segment: LightShowSegment) => { await start(segment, props.show()); emit('started'); });
const end = props.status.action(async () => { await stop(); });
const line = computed(() => test.value ? `Playing “${playing.value}”${props.where ?? ''}` : unavailable.value);
</script>

<template>
  <div class="button-group">
    <button v-for="segment in segments" :key="segment.id" :disabled="busy || !lightsFree || !hasBoards" @click="play(segment.id)"><component :is="segment.icon" />{{ segment.label }}</button>
    <IconButton class="danger" :icon="Square" label="Stop test" :disabled="busy || !test" @click="end" />
  </div>
  <p v-if="line" class="test-status" role="status" :data-testid="testid">{{ line }}</p>
</template>
