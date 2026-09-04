# Running and debugging in Docker

Everything here is `docker compose` on one host, which is the v1 target — Kubernetes and CI/CD are
explicitly out of scope (`Architect Files/04-Infrastructure.md` §8).

## Quick start

```bash
docker compose up --build          # db + API (dotnet watch) + Vite dev server
```

Open **<http://localhost:5173>** and sign in:

| | |
|---|---|
| Email | `admin@accountantapp.local` |
| Password | `development-admin-password` |

The seeded account is **forced to change its password at first login**, so the first screen after
signing in is `/change-password`. That is not a bug — an environment variable is visible in
`docker inspect` and in shell history, so the seeded password is treated as compromised by design.

Use **Chrome, Edge or Firefox**, and use `localhost` rather than `127.0.0.1`. The session cookie is
`Secure` unconditionally (`CookieSecurePolicy.Always`, because Caddy terminates TLS in production and
`SameAsRequest` would silently drop the flag in the one environment that needs it). Browsers accept a
`Secure` cookie over plain HTTP **only** for `localhost`, treated as a trustworthy origin. Safari has
historically refused it. The symptom, if you hit it, is signing in successfully and landing back on the
login screen — the cookie was set and then discarded.

```bash
docker compose down                # stop; the database survives
docker compose down -v             # stop AND destroy the database and the cookie keys
docker compose logs -f api         # startup failures land here
docker compose exec db psql -U postgres -d accountant_app
```

## What runs where

| | Development (`docker-compose.yml`) | Production (`docker-compose.prod.yml`) |
|---|---|---|
| Database | `db`, published to `127.0.0.1:5432` | `db`, **no published port** |
| API | `api`, `dotnet watch`, `127.0.0.1:5131` | inside `app`, no published port |
| SPA | `ui`, Vite dev server, `127.0.0.1:5173` | **static files inside `app`**, no container |
| Proxy | none | `caddy`, the only published ports (80, 443) |

The production topology is LOCKED at three containers with the SPA served from inside the API
(§1), so that there is one origin, no CORS anywhere, and `SameSite=Strict` works with no exceptions.
Development is the one place the SPA runs separately, because hot reload is worth it (§2).

In both cases the browser sees **one origin**. In development the Vite dev server proxies `/api` to
the API; in production Caddy proxies everything to `app`. The SPA always calls `/api/...` relative to
its own origin and there is deliberately no API base URL variable in it — that is how one build ends
up pointing at another instance.

`Architect Files/04-Infrastructure.md` §2 describes development as three *processes* (Postgres in a
container, `dotnet run` and `npm run dev` on the host). **That still works** and nothing here breaks
it: `docker compose up db` alone is exactly the §2 arrangement, and `vite.config.ts` still defaults its
proxy to `http://localhost:5131`. The containerised API and dev server are an addition, for machines
that have Docker rather than the .NET SDK and Node.

## Debugging in VS Code

Install the **C# extension** (`ms-dotnettools.csharp`) — it provides the `coreclr` debug type. Then
press **F5** and pick **“Debug in Docker (API + UI)”**. That brings the stack up, attaches the .NET
debugger inside the `api` container, and opens Chrome with TypeScript breakpoints working.

For the API alone, use **“Docker: Attach to API”** and choose the one `dotnet` process in the list.

Breakpoints work in both `.cs` and `.tsx` files. The C# debugger runs `vsdbg` *inside* the container
and VS Code talks to it over `docker exec`; `sourceFileMap` maps the container's `/src` back to this
folder, which is what makes a breakpoint bind rather than showing as a hollow circle.

**Debugging swaps `dotnet watch` for a single process.** `docker-compose.debug.yml` builds once and
runs the produced assembly directly, because `dotnet watch` is two processes — ambiguous in the
process picker — and a hot reload replaces the code under an attached debugger, so breakpoints resolve
against a build that is no longer running. The cost: a C# change needs
**`docker compose -f docker-compose.yml -f docker-compose.debug.yml restart api`**, which is the
“docker: restart api” task. The SPA still hot-reloads normally.

While you sit on a breakpoint the `api` container is eventually reported **unhealthy** — a paused
process does not answer the healthcheck. Nothing acts on it and it clears when you continue.

## Running the production stack locally

Worth doing before deploying, because it exercises three things the development stack cannot: the SPA
served from the API's `wwwroot` at the same origin, the `Secure` `SameSite=Strict` cookie behind a real
proxy, and Caddy's rate limit on `/api/auth/*`.

```bash
cp .env.example .env      # then fill it in
docker compose -f docker-compose.prod.yml up -d --build
```

For a local run set `SITE_ADDRESS=http://localhost` and `APP_BASE_URL=http://localhost`. A bare
hostname turns on automatic TLS, and Caddy will try to obtain a real certificate from Let's Encrypt —
which cannot succeed for a machine that is not publicly reachable on that name, and failed attempts
count against a weekly per-domain limit.

`ACCOUNTANT_ADMIN_PASSWORD` must satisfy the normal password policy. It is validated *before* the
account is written, so a rejected password fails startup rather than creating an account nobody can
sign in to.

The first Caddy build compiles Caddy from source (about a minute) and is then cached — see
`caddy/Dockerfile` for why the stock image will not do.

## Departures from `04-Infrastructure.md`, and why

Every one of these is a case where following the document literally produces something that does not
start. The document is the spec; these are the places it has drifted from the code that now exists.

1. **`node:22-alpine`, not `node:20-alpine`** (§3). The locked Vite is 8.2.2 and declares
   `engines: { node: "^20.19.0 || >=22.12.0" }`. The `node:20-alpine` tag only works while it happens
   to resolve to 20.19.x.
2. **A `.dockerignore` exists, and it is load-bearing.** §3's Dockerfile runs `npm ci` and then
   `COPY frontend/ ./`, which without an exclusion overwrites the container's install with the host's
   Windows tree — `vite build` then dies on `@esbuild/win32-x64`. `bin/` and `obj/` are committed in
   this repository, so they are in the build context too.
3. **Caddy is built, not pulled** (§3 says `caddy:2-alpine`). The `rate_limit` directive is not in
   Caddy's standard build and Caddy rejects an unknown directive at startup, so the stock image plus
   §3's Caddyfile gives a proxy that never starts. §3 anticipates this; compiling the module keeps a
   rate limit the README makes mandatory.
4. **`app_keys:/app/keys` volume in the production compose.** §4 requires the data-protection keys to
   persist; §3's compose file has no volume for them. Without it the container refuses to start, since
   `DataProtection:KeyPath` must be writable.
5. **`App__BaseUrl` in the production compose.** Absent from §3's environment list and required —
   invitation and password-reset links are built from it, and those tokens are single-use.
6. **`ForwardedHeaders__KnownNetworks__0` is set, and the compose subnets are pinned.** `Program.cs`
   builds an explicit allow-list and honours `X-Forwarded-For` from nothing else; with the list empty
   every audit row records Caddy's address instead of the caller's. Docker would otherwise allocate a
   different subnet per host.
7. **`X-Forwarded-Proto` is set on the `/api/auth/*` route too.** §3's Caddyfile sets it only on the
   general `reverse_proxy`, which leaves the requests that *set the session cookie* looking like plain
   HTTP to the application.
8. **`docker-compose.prod.yml`, not `docker-compose.yml`.** §2 and §3 both name their file
   `docker-compose.yml`; two files cannot share a name. The development stack took the default,
   being the one run repeatedly.
9. **`--no-launch-profile` on the container's `dotnet run`.** `launchSettings.json` sets
   `applicationUrl=http://localhost:5131`, and a launch profile overrides `ASPNETCORE_URLS` — so
   without the flag the API binds `localhost` inside the container and is unreachable from anywhere.
10. **The API's dev port is 5131, not the 5000 in §2.** `launchSettings.json` has always said 5131.
    `BACKEND_CHANGES_REQUIRED.md` item 8.
