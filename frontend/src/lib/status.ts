import { computed, ref } from 'vue';
import { messageOf } from './api';

/**
 * The status line of a page and the actions that write to it. An action clears the line when it starts and leaves
 * what it returned, or why it failed. The page is busy while any of its actions runs.
 */
export function usePageStatus() {
  const message = ref(''), error = ref(''), running = ref(0);
  function clear() { message.value = ''; error.value = ''; }
  function action<Arguments extends unknown[]>(run: (...args: Arguments) => Promise<string | void> | string | void) {
    return async (...args: Arguments) => {
      clear(); running.value++;
      try { message.value = (await run(...args)) || message.value; }
      catch (failure) { error.value = messageOf(failure); }
      finally { running.value--; }
    };
  }
  return {
    message, error, clear, action,
    busy: computed(() => running.value > 0),
    failed: computed(() => !!error.value),
    /** The failure if there is one, otherwise the message. */
    text: computed(() => error.value || message.value),
  };
}
export type PageStatus = ReturnType<typeof usePageStatus>;
