import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig(({ command }) => ({
  plugins: [vue()],
  base: command === 'serve' ? '/' : '/ui/',
  build: {
    outDir: '../BobFx.Core/wwwroot/ui',
    emptyOutDir: true,
    target: 'safari17',
    // The optional Three.js renderer is isolated in its own lazy chunk.
    chunkSizeWarningLimit: 600,
  },
  server: {
    proxy: {
      '/api': { target: 'http://127.0.0.1:5159', changeOrigin: false },
      '/hubs': { target: 'http://127.0.0.1:5159', changeOrigin: false, ws: true },
      '/sounds': { target: 'http://127.0.0.1:5159', changeOrigin: false },
      '/favicon.png': { target: 'http://127.0.0.1:5159', changeOrigin: false },
    },
  },
}));
