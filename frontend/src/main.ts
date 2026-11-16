import { createApp } from 'vue';
import { createRouter, createWebHistory } from 'vue-router';
import { VueQueryPlugin } from '@tanstack/vue-query';
import '@fontsource-variable/space-grotesk';
import './style.css';
import App from './App.vue';
import { routes } from './lib/routes';
import { queryClient } from './lib/session';

const router = createRouter({ history: createWebHistory(), routes, scrollBehavior: () => ({ top: 0 }) });
router.afterEach(to => { document.title = `${to.meta.title ?? 'Not found'} | BobFX`; });
createApp(App).use(router).use(VueQueryPlugin, { queryClient }).mount('#app');
