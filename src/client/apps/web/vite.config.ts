import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

const r = (p: string) => fileURLToPath(new URL(p, import.meta.url));

// The built app is served by the KITE server from its wwwroot folder (no internet needed on the LAN).
export default defineConfig({
  root: r('.'),
  plugins: [react()],
  resolve: {
    alias: {
      '@kite/ui': r('../../packages/ui/src/index.tsx'),
      '@kite/i18n': r('../../packages/i18n/src/index.ts'),
      '@kite/api': r('../../packages/api-client/src/index.ts'),
    },
  },
  build: { outDir: r('./dist'), emptyOutDir: true, sourcemap: true },
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5080', '/health': 'http://localhost:5080' },
  },
});
