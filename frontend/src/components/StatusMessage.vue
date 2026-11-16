<script setup lang="ts">
import type { PageStatus } from '../lib/status';

// A page's status line: what its last action said, shown as a failure when the action failed.
defineProps<{ status: PageStatus }>();
</script>

<template>
  <p v-if="status.text.value" role="status" :class="{ 'error-message': status.failed.value }">{{ status.text.value }}</p>
</template>
