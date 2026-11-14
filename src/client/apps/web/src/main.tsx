import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createHttpApi } from '@kite/api';
import { App } from './App';

async function start() {
  // VITE_MOCK=1 runs the app against an in-memory server, for demos and design review only.
  const api = import.meta.env.VITE_MOCK
    ? (await import('./mock')).createMockApi({ configured: import.meta.env.VITE_MOCK !== 'setup' })
    : createHttpApi();
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App api={api} />
    </StrictMode>,
  );
}

void start();
