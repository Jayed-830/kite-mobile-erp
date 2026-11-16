import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createHttpApi } from '@kite/api';
import { App } from './App';
import { isEmergencyMode } from './pos/printing';

async function start() {
  // Emergency mode: the Windows app runs this bundled copy with a local API over its encrypted store (6.4).
  // VITE_MOCK=1 runs the app against an in-memory server, for demos and design review only.
  const emergency = isEmergencyMode();
  const api = emergency
    ? (await import('./pos/emergencyApi')).createEmergencyApi(window.kiteDesktop!.emergency!)
    : import.meta.env.VITE_MOCK
      ? (await import('./mock')).createMockApi({ configured: import.meta.env.VITE_MOCK !== 'setup' })
      : createHttpApi();
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App api={api} initialPath={emergency ? '/pos' : undefined} />
    </StrictMode>,
  );
}

void start();
