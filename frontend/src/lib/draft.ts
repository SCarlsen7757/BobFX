import { computed, ref, watch, type Ref } from 'vue';
import { clone, useInstallationState } from './installation';
import { useUnsavedGuard } from './unsaved';
import type { InstallationSettings } from './contracts';

/** Whether two JSON values hold the same data, whatever order their properties were written in. */
export function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) || Array.isArray(b))
    return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((item, index) => sameValue(item, b[index]));
  const left = a as Record<string, unknown>, right = b as Record<string, unknown>;
  const keys = Object.keys(left).filter(key => left[key] !== undefined);
  return keys.length === Object.keys(right).filter(key => right[key] !== undefined).length && keys.every(key => sameValue(left[key], right[key]));
}

/**
 * The part of the installation that a settings page edits. The draft starts as a copy of what is saved and is the
 * page's own from then on: later server updates do not replace it, so nothing typed is lost. The page asks before
 * it is left with unsaved changes; call this before any other leave guard of the page.
 *
 * @param select The part of the settings this page saves.
 * @param pending What Save would send, when that is more than the draft, such as a form that is still open.
 */
export function useSectionDraft<T>(select: (settings: InstallationSettings) => T, pending?: { value: (draft: T) => T; also: () => boolean }) {
  const { settings } = useInstallationState();
  const saved = computed(() => settings.value && select(settings.value));
  const draft = ref<T>() as Ref<T | undefined>;
  /** Starts again from what is saved; also used after a save, to continue from what the server stored. */
  function adopt() { if (saved.value !== undefined) draft.value = clone(saved.value); }
  watch(saved, () => { if (draft.value === undefined) adopt(); }, { immediate: true });
  const dirty = computed(() => draft.value !== undefined && saved.value !== undefined &&
    (!sameValue(pending ? pending.value(draft.value) : draft.value, saved.value) || !!pending?.also()));
  useUnsavedGuard(dirty);
  return { draft, dirty, adopt };
}
