import { computed } from 'vue';
import { endpoints } from './endpoints';
import { useCanConfigure, useInstallationState } from './installation';
import { segments } from './segments';
import { command, connected } from './session';
import type { AnimationSettings, LightShowSegment, LightShowSettings } from './contracts';

/** A part of the light show playing on the real boards: what is playing, and starting and stopping it. */
export function useLightShowTest() {
  const { settings, lightShowTest: test } = useInstallationState();
  // A running test does not hold the lights against another test: the next one replaces it.
  const lightsFree = useCanConfigure(true);
  const hasBoards = computed(() => !!settings.value?.boards.some(board => board.deviceMac));
  return {
    test, lightsFree, hasBoards,
    playing: computed(() => segments.find(segment => segment.id === test.value?.segment)?.label),
    /** Why a test cannot be started now; empty when it can. */
    unavailable: computed(() => !hasBoards.value ? 'No board has a controller.' : !lightsFree.value && connected.value ? 'The lights are in use.' : ''),
    start: (segment: LightShowSegment, show: { lightShow: LightShowSettings; animation: AnimationSettings }) =>
      command(endpoints.installation.lightShowTest, { segment, lightShow: show.lightShow, animation: show.animation }),
    stop: () => command(endpoints.installation.lightShowTest, undefined, 'DELETE'),
  };
}
