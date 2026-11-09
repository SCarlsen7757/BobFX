<script setup lang="ts">
defineProps<{ enabled: boolean; disabled: boolean; status: string }>();
defineEmits<{ toggle: [] }>();
</script>

<template>
  <div class="audio-controls">
    <button class="audio-toggle" :disabled="disabled" :aria-pressed="enabled"
      :aria-label="enabled ? 'Mute audio' : 'Enable audio'" :title="enabled ? 'Mute audio' : 'Enable audio'" @click="$emit('toggle')">
      <svg aria-hidden="true" viewBox="0 0 24 24">
        <path d="m11 5-6 4H2v6h3l6 4V5Z" />
        <path v-if="enabled" d="M15 8a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14" />
        <path v-else d="m16 9 6 6m0-6-6 6" />
      </svg>
    </button>
    <span v-if="status" class="audio-status" role="status">{{ status }}</span>
  </div>
</template>
