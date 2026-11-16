<script setup lang="ts">
import { RotateCcw, Save } from '@lucide/vue';
import IconButton from './IconButton.vue';

// The save row of a settings page. It stays at the bottom of the window while the page scrolls, and lights up
// with "Unsaved changes" as soon as the draft differs from what is saved, so Save cannot be scrolled out of sight.
// `note` repeats the page's status message next to the button, where the eyes are after pressing Save; the page
// keeps the announced copy at its top, so this one is hidden from screen readers.
defineProps<{ dirty: boolean; label: string; disabled?: boolean; reloadLabel?: string; reloadDisabled?: boolean; note?: string; failed?: boolean }>();
defineEmits<{ save: []; reload: [] }>();
</script>

<template>
  <div class="save-bar" :class="{ dirty }">
    <slot />
    <div class="save-bar-row">
      <button class="primary" :disabled="disabled" :aria-label="label" :title="label" @click="$emit('save')"><Save />Save</button>
      <IconButton v-if="reloadLabel" :icon="RotateCcw" :label="reloadLabel" :disabled="reloadDisabled" @click="$emit('reload')" />
      <span v-if="dirty" class="unsaved" role="status">Unsaved changes</span>
      <span v-if="note" class="save-note" :class="{ failed }" aria-hidden="true">{{ note }}</span>
    </div>
  </div>
</template>
