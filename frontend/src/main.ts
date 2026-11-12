import { createApp } from 'vue';
import { createRouter, createWebHistory } from 'vue-router';
import { VueQueryPlugin } from '@tanstack/vue-query';
import '@fontsource-variable/space-grotesk';
import './style.css';
import App from './App.vue';
import Game from './pages/Game.vue';
import Countdown from './pages/Countdown.vue';
import LightShow from './pages/LightShow.vue';
import Effects from './pages/Effects.vue';
import Devices from './pages/Devices.vue';
import BoardLayout from './pages/BoardLayout.vue';
import Controllers from './pages/Controllers.vue';
import { queryClient } from './lib/session';

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: Game, meta: { title: 'Game' } },
    { path: '/game/fullscreen', component: Game, meta: { title: 'Game', fullscreen: true } },
    { path: '/countdown', component: Countdown, meta: { title: 'Countdown settings' } },
    { path: '/lights', component: LightShow, meta: { title: 'Light show' } },
    { path: '/drgb', component: Effects, meta: { title: 'LED effects' } },
    { path: '/wled', component: Devices, meta: { title: 'WLED devices' } },
    { path: '/layout', component: BoardLayout, meta: { title: 'Board layout' } },
    { path: '/controllers', component: Controllers, meta: { title: 'Remote controllers' } },
    { path: '/:pathMatch(.*)*', component: { template: '<h1>Page not found</h1><p><a href="/">Return to the game</a></p>' } },
  ],
  scrollBehavior: () => ({ top: 0 }),
});
router.afterEach(to => { document.title = `${to.meta.title ?? 'Not found'} | BobFX`; });
createApp(App).use(router).use(VueQueryPlugin, { queryClient }).mount('#app');
