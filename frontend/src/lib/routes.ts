import type { Component } from 'vue';
import type { RouteRecordRaw } from 'vue-router';
import { Columns3, Gamepad2, Joystick, MonitorPlay, Router, Sparkles, Timer, WandSparkles } from '@lucide/vue';
import BoardLayout from '../pages/BoardLayout.vue';
import Boards from '../pages/Boards.vue';
import Controllers from '../pages/Controllers.vue';
import Countdown from '../pages/Countdown.vue';
import Devices from '../pages/Devices.vue';
import Effects from '../pages/Effects.vue';
import Game from '../pages/Game.vue';
import LightShow from '../pages/LightShow.vue';
import NotFound from '../pages/NotFound.vue';

export const gamePath = '/', fullscreenPath = '/game/fullscreen';

/**
 * The pages in the order of the navigation. The router and the navigation are both built from this list. The
 * server serves the app for these same paths, listed in Program.cs, and answers 404 for any other.
 */
export const pages: Array<{ path: string; title: string; icon: Component; component: Component }> = [
  { path: gamePath, title: 'Game', icon: Gamepad2, component: Game },
  { path: '/countdown', title: 'Countdown settings', icon: Timer, component: Countdown },
  { path: '/lights', title: 'Light show', icon: Sparkles, component: LightShow },
  { path: '/drgb', title: 'LED effects', icon: WandSparkles, component: Effects },
  { path: '/wled', title: 'WLED devices', icon: Router, component: Devices },
  { path: '/layout', title: 'Board layout', icon: Columns3, component: BoardLayout },
  { path: '/boards', title: 'Live boards', icon: MonitorPlay, component: Boards },
  { path: '/controllers', title: 'Remote controllers', icon: Joystick, component: Controllers },
];

export const routes: RouteRecordRaw[] = [
  ...pages.map(page => ({ path: page.path, component: page.component, meta: { title: page.title } })),
  { path: fullscreenPath, component: Game, meta: { title: 'Game', fullscreen: true } },
  { path: '/:pathMatch(.*)*', component: NotFound },
];
