import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import CssBaseline from '@mui/material/CssBaseline';
import { ThemeProvider } from '@mui/material/styles';
import { QueryClientProvider } from '@tanstack/react-query';
import { App } from './App';
import { queryClient } from './shared/api/queryClient';
import { SessionProvider } from './shared/auth/SessionProvider';
import { theme } from './theme';

/**
 * createRoot, the provider stack, <App />. Nothing else: no side effects, no configuration reading,
 * no console.log (plan section 7.3).
 *
 * THE ORDER IS NOT ARBITRARY:
 *
 *   QueryClientProvider   value={queryClient}
 *     ThemeProvider       theme={theme}
 *       CssBaseline
 *         SessionProvider
 *           App
 *
 * SessionProvider sits INSIDE QueryClientProvider because its bootstrap is a useQuery and its 401
 * handler calls queryClient.clear(). Inverting those two throws "No QueryClient set" on the first
 * render, which reads as a broken installation rather than as a provider-order mistake.
 *
 * It sits OUTSIDE the router -- which lives in App -> RouterProvider -- because RequireSession reads
 * the session on every route, including the public ones.
 *
 * There is no API base URL to read here, and no VITE_ variable to plumb through: every path is a
 * relative string beginning /api/ and the dev proxy in vite.config.ts makes that one origin
 * (04-Infrastructure.md section 2 forbids a base-URL variable by name -- "A base-URL variable is how
 * the same build ends up pointing at the wrong instance").
 */
const container = document.getElementById('root');
if (container === null) {
  // index.html ships with <div id="root"></div>. If it is missing, failing loudly beats rendering
  // into a detached node and showing a blank page with a clean console.
  throw new Error('Root container #root was not found in index.html.');
}

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={theme}>
        {/* Nested rather than self-closing, matching the provider stack above exactly. CssBaseline
            renders its children unchanged; the nesting is what makes the tree readable as one
            chain. */}
        <CssBaseline>
          <SessionProvider>
            <App />
          </SessionProvider>
        </CssBaseline>
      </ThemeProvider>
    </QueryClientProvider>
  </StrictMode>,
);
