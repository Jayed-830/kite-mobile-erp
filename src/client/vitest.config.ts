import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

const r = (p: string) => fileURLToPath(new URL(p, import.meta.url));

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@kite/ui': r('./packages/ui/src/index.tsx'),
      '@kite/i18n': r('./packages/i18n/src/index.ts'),
      '@kite/api': r('./packages/api-client/src/index.ts'),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: [r('./apps/web/src/test/setup.ts')],
    include: ['packages/*/src/**/*.test.{ts,tsx}', 'apps/web/src/**/*.test.{ts,tsx}'],
    css: false,
  },
});
