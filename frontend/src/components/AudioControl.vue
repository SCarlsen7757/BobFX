<script setup lang="ts">
import { Volume2, VolumeX } from '@lucide/vue';

defineProps<{ enabled: boolean; disabled: boolean; status: string }>();
defineEmits<{ toggle: [] }>();
</script>

<template>
  <div class="audio-controls">
    <button class="audio-toggle" :disabled="disabled" :aria-pressed="enabled"
      :aria-label="enabled ? 'Mute audio' : 'Enable audio'" :title="enabled ? 'Mute audio' : 'Enable audio'" @click="$emit('toggle')">
      <Volume2 v-if="enabled" /><VolumeX v-else />
    </button>
    <span v-if="status" class="audio-status" role="status">{{ status }}</span>
  </div>
</template>
