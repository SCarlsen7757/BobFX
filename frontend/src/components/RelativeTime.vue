<script setup lang="ts">
import { computed } from 'vue';
import { useNow } from '../lib/lifecycle';

// "3 minutes ago", kept up to date. It ticks by itself, so the page around it does not re-render every second.
const props = defineProps<{ value: string }>();
const now = useNow();
const date = computed(() => new Date(props.value));
const text = computed(() => {
  const elapsed = Math.max(0, Math.floor((now.value - date.value.getTime()) / 1000));
  if (!Number.isFinite(elapsed)) return 'Unknown';
  if (elapsed < 5) return 'Just now';
  const [amount, unit] = elapsed < 60 ? [elapsed, 'second'] : elapsed < 3600 ? [Math.floor(elapsed / 60), 'minute']
    : elapsed < 86400 ? [Math.floor(elapsed / 3600), 'hour'] : [Math.floor(elapsed / 86400), 'day'];
  return `${amount} ${unit}${amount === 1 ? '' : 's'} ago`;
});
</script>

<template>
  <time :datetime="value" :title="date.toLocaleString()">{{ text }}</time>
</template>
