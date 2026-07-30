import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Dev proxy target: the Code Exploder gateway. Override with VITE_PROXY_TARGET when
// the backend runs on a different port.
const target = process.env.VITE_PROXY_TARGET ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      // Split the large, rarely-changing signalr vendor into its own long-cached chunk.
      output: {
        manualChunks: {
          signalr: ['@microsoft/signalr'],
        },
      },
    },
  },
  server: {
    proxy: {
      '/api': { target, changeOrigin: true },
      '/hubs': { target, changeOrigin: true, ws: true },
      '/healthz': { target, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['src/test/setup.ts'],
    globals: true,
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
