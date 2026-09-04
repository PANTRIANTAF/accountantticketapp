import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * `process` declared locally, INSTEAD OF adding @types/node.
 *
 * tsconfig.json has `"include": ["src", "vite.config.ts"]`, so this file is type-checked by
 * `npm run build` -- and without a declaration `process.env` is TS2591, whose own error text tells you
 * to install @types/node. That would be a fourth devDependency outside GeneralUIArchitecture.md
 * section 1.5's locked list, and a lockfile change, to type two reads of one global.
 *
 * Narrower than the real Node typing on purpose: this states exactly what is used here, so the
 * declaration cannot quietly license `process.cwd()` or `process.exit()` in a config that Vite also
 * loads in environments where those are not wanted. Vite loads this file in Node, so `process` exists
 * at run time; nothing under src/ can see this declaration, and nothing there reads `process` or
 * `import.meta.env` at all.
 */
declare const process: { readonly env: Readonly<Record<string, string | undefined>> };

/**
 * Where the dev server forwards /api.
 *
 * THIS IS NOT THE FORBIDDEN `VITE_API_URL`. 04-Infrastructure.md section 2 bans an API base URL
 * variable in the SPA, because it is how one build ends up pointing at the wrong instance. That ban
 * is about client code, and this is not client code:
 *
 *   - it has no `VITE_` prefix, so Vite cannot expose it to the browser bundle,
 *   - it is read by Node, in this config file, at dev-server startup,
 *   - nothing under src/ reads `import.meta.env`, and the SPA still calls `/api/...` relative to its
 *     own origin in every environment.
 *
 * It exists because the dev server has two places to forward to depending on where it is running:
 * the API on the host, or the `api` service on the compose network. The default is the host case, so
 * `npm run dev` on a machine with the .NET SDK behaves exactly as it did before this variable existed.
 *
 * The port is 5131, not the 5000 in 04-Infrastructure.md section 2:
 * AccountantApp.Api/Properties/launchSettings.json binds http://localhost:5131 on both profiles, and
 * docker-compose.yml publishes the container's 8080 to 5131 to match. A proxy pointed at a dead port
 * fails every call with no useful error. BACKEND_CHANGES_REQUIRED item 8.
 */
const apiProxyTarget = process.env.DEV_API_PROXY_TARGET ?? 'http://localhost:5131';

/**
 * File-change polling, for the container case only.
 *
 * Docker Desktop on Windows does not propagate inotify events across a bind mount, so with the
 * default watcher Vite reports that it is watching and then never reacts to a save. Polling costs CPU
 * and is therefore off unless docker-compose.yml asks for it.
 */
const usePolling = process.env.DEV_WATCH_POLLING === 'true';

export default defineConfig({
  plugins: [react()],
  server: {
    // The proxy is what lets the browser see ONE origin in development, so the SameSite=Strict
    // session cookie works and no CORS configuration is needed anywhere. Removing it and calling
    // the API directly on its own port breaks the cookie, not just convenience.
    proxy: { '/api': apiProxyTarget },

    // Fixed, and strict. The port is not a detail: App__BaseUrl is set to http://localhost:5173 in
    // docker-compose.yml, and invitation and password-reset emails are built from it. If Vite
    // silently moved to 5174 because something else held 5173, those links would point at an origin
    // with nothing on it -- and the tokens are single-use, so each one is spent finding that out.
    port: 5173,
    strictPort: true,

    // `host` is deliberately NOT set here. In the container the dev server must bind 0.0.0.0, and
    // frontend/Dockerfile.dev passes `--host 0.0.0.0` on the command line for exactly that. Setting
    // it in the config would also apply to a host-process `npm run dev`, publishing the dev server
    // to every machine on the local network.

    ...(usePolling ? { watch: { usePolling: true, interval: 300 } } : {}),
  },
  build: {
    // The Dockerfile copies /ui/dist into the API's wwwroot (04-Infrastructure.md section 3).
    outDir: 'dist',
  },
});
