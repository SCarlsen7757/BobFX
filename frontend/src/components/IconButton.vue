<script setup lang="ts">
import type { Component } from 'vue';

// A button that shows an icon instead of words. `label` names it for screen readers and is its tooltip.
defineProps<{ icon: Component; label: string }>();
</script>

<template>
  <button class="icon-button" :aria-label="label" :title="label"><component :is="icon" /><slot /></button>
</template>
