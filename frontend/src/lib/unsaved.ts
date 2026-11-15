import { onBeforeUnmount, type Ref } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';

/**
 * Asks before a settings page with unsaved changes is left, whether through the navigation or by closing or
 * reloading the tab. Call it before any other leave guard of the page, so that staying leaves everything as it is.
 */
export function useUnsavedGuard(dirty: Ref<boolean>) {
  onBeforeRouteLeave(() => !dirty.value || confirm('Leave without saving your changes?'));
  // The browser shows its own wording here; a page can only say that it has something to lose.
  const warn = (event: BeforeUnloadEvent) => { if (dirty.value) event.preventDefault(); };
  window.addEventListener('beforeunload', warn);
  onBeforeUnmount(() => window.removeEventListener('beforeunload', warn));
}
