import { onBeforeUnmount, onMounted, ref } from 'vue';

/**
 * Runs `leave` when the document is going away, which a route guard does not see: a closed tab, a reload, a phone
 * putting the browser in the background.
 */
export function useOnPageHide(leave: () => void) {
  window.addEventListener('pagehide', leave);
  onBeforeUnmount(() => window.removeEventListener('pagehide', leave));
}

/** The current time, refreshed every second while the component is shown. */
export function useNow() {
  const now = ref(Date.now());
  let timer: ReturnType<typeof setInterval> | undefined;
  onMounted(() => { timer = setInterval(() => { now.value = Date.now(); }, 1000); });
  onBeforeUnmount(() => clearInterval(timer));
  return now;
}
