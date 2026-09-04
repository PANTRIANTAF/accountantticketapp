# Infrastructure & Deployment

## 1. Container topology — LOCKED

**Production is three containers.** This is settled; do not reintroduce the alternative.

| Container | Contents | Publishes a port |
|---|---|---|
| `caddy` | Reverse proxy. TLS termination, HTTP→HTTPS redirect, rate limiting on auth endpoints. | **Yes — 80 and 443. The only one.** |
| `app` | The ASP.NET Core API **and the built React SPA served from inside it** as static files. | No |
| `db` | PostgreSQL 16. | No |

```
                    internet
                       │
                   :443 │ :80
              ┌────────▼────────┐
              │     caddy       │   TLS, redirect, rate limit
              └────────┬────────┘
                       │ http (internal network only)
              ┌────────▼────────┐
              │      app        │   /api/*  → Minimal API
              │  (API + SPA)    │   /*      → index.html (SPA fallback)
              └────────┬────────┘
                       │ 5432 (internal network only)
              ┌────────▼────────┐
              │       db        │   PostgreSQL 16
              └─────────────────┘
```

### Why the SPA lives in the `app` container

- **One deployment serves one accounting Office** (see README, *Deployment model*). There is no
  scaling pressure that would justify deploying the UI independently of the API.
- **Same origin, so CORS is disabled in production.** A separate UI container puts the SPA on a
  different origin and forces CORS configuration — one more thing to get wrong on an
  internet-facing app holding one accountant's entire client book.
- **The session cookie is `SameSite=Strict`.** Same-origin makes that work with no exceptions.
- **It is cheap to reverse.** Every API route is under `/api` and nothing else is
  (`App/GeneralAppArchitecture.md` §8). Splitting the SPA into its own container later means
  adding one `location /` block to the Caddy config — no code change.

The cost is real and accepted: a CSS-only fix rebuilds and redeploys the `app` image.

### Serving the SPA from the API

In `Program.cs`, after the API routes are mapped:

```csharp
app.UseDefaultFiles();      // "/" → index.html
app.UseStaticFiles();       // wwwroot/, populated by the React build
app.MapFallbackToFile("index.html");   // client-side routing: /tickets/42 → index.html
```

`MapFallbackToFile` must be registered **last**, after every `MapXxxEndpoints()` call, or it
swallows API routes. It must never return `index.html` for a path under `/api` — an unknown
API route is a `404`, not an HTML page. Verify both: `GET /tickets/42` returns HTML,
`GET /api/nonexistent` returns a `404` `ProblemDetails`.

## 2. Development environment

Development is the one place the SPA runs separately, because hot reload is worth it. Three
processes, not three production containers:

| Process | How | Port |
|---|---|---|
| PostgreSQL | Container (`docker compose up db`) | 5432, published to localhost only |
| API | `dotnet run` on the host | 5000 |
| React dev server | `npm run dev` on the host | 5173 |

The dev server proxies `/api` to the API, so the browser still sees a single origin and no
CORS is needed even in development. In `vite.config.ts`:

```ts
export default defineConfig({
  server: {
    proxy: { '/api': 'http://localhost:5000' },
  },
});
```

**Do not** set an API base URL environment variable in the SPA (`VITE_API_URL` or similar).
The SPA always calls `/api/...` relative to its own origin, in every environment. A base-URL
variable is how the same build ends up pointing at the wrong instance.

`docker-compose.yml` for development — database only:

```yaml
services:
  db:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: postgres
      POSTGRES_DB: accountant_app
    ports:
      - "127.0.0.1:5432:5432"     # localhost only, never 0.0.0.0
    volumes:
      - db_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  db_data:
```

## 3. Production deployment

### The `app` image

One multi-stage build produces one image containing both the API and the SPA. The SPA build
output lands in the API's `wwwroot`.

```dockerfile
# Dockerfile
FROM node:20-alpine AS ui
WORKDIR /ui
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build                      # emits /ui/dist

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY AccountantApp.Api/*.csproj AccountantApp.Api/
RUN dotnet restore AccountantApp.Api/AccountantApp.Api.csproj
COPY AccountantApp.Api/ AccountantApp.Api/
RUN dotnet publish AccountantApp.Api/AccountantApp.Api.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api /publish ./
COPY --from=ui /ui/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID                          # do not run as root
ENTRYPOINT ["dotnet", "AccountantApp.Api.dll"]
```

The target framework is **.NET 10**. Keep the SDK/runtime image tags, the `csproj`
`TargetFramework`, and this file in agreement — a mismatch fails at build, which is the good
outcome, but only if all three are updated together.

The SQL migration scripts must reach the published output, or the runner
(`App/GeneralAppArchitecture.md` §6) finds nothing at startup. In `AccountantApp.Api.csproj`:

```xml
<ItemGroup>
  <Content Include="Slices/**/Infrastructure/Migrations/*.sql" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Verify this after the first deployment: if `schema_versions` is empty and the app started
anyway, the scripts were not copied.

### `docker-compose.yml` (production)

```yaml
services:
  db:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: accountant_app
    volumes:
      - db_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER}"]
      interval: 10s
      timeout: 5s
      retries: 5
    networks: [backend]
    restart: unless-stopped
    # No ports. Reachable only from `app`, over the `backend` network.

  app:
    build: .
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Default: Host=db;Database=accountant_app;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
      ACCOUNTANT_ADMIN_EMAIL: ${ACCOUNTANT_ADMIN_EMAIL}
      ACCOUNTANT_ADMIN_PASSWORD: ${ACCOUNTANT_ADMIN_PASSWORD}
    depends_on:
      db:
        condition: service_healthy
    networks: [backend, frontend]
    restart: unless-stopped
    # No ports. Reachable only from `caddy`, over the `frontend` network.

  caddy:
    image: caddy:2-alpine
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data          # ACME certificates — persist or you will re-issue on every restart
      - caddy_config:/config
    depends_on: [app]
    networks: [frontend]
    restart: unless-stopped

networks:
  backend:
    internal: true     # no route to the internet; db cannot be reached from outside
  frontend:

volumes:
  db_data:
  caddy_data:
  caddy_config:
```

Two things this file encodes deliberately:

- **Only `caddy` has a `ports:` entry.** `db` and `app` publish nothing. `db` sits on an
  `internal: true` network that `caddy` is not attached to, so a compromised proxy still has
  no route to PostgreSQL.
- **No secret is written in the file.** Every credential comes from the environment.

### `Caddyfile`

```
accountant.example.com {
    encode gzip

    # Rate limit the auth endpoints. Not hardening — mandatory (see README).
    # Requires the caddy-ratelimit plugin; if unavailable, do it in the API instead.
    route /api/auth/* {
        rate_limit {
            zone auth {
                key    {remote_host}
                events 10
                window 1m
            }
        }
        reverse_proxy app:8080
    }

    reverse_proxy app:8080 {
        header_up X-Forwarded-Proto https
    }

    header {
        Strict-Transport-Security "max-age=31536000; includeSubDomains"
        X-Content-Type-Options    "nosniff"
        X-Frame-Options           "DENY"
        Referrer-Policy           "no-referrer"
        -Server
    }
}
```

Caddy obtains and renews TLS certificates automatically; HTTP→HTTPS redirect is its default.

Because the API sits behind a proxy, it must trust the forwarded headers or every audit entry
records the proxy's address instead of the caller's, and `Secure` cookie handling misbehaves.
In `Program.cs`, before any other middleware:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownNetworks = { /* the compose `frontend` subnet */ },
});
```

## 4. Configuration and secrets

Everything environment-specific is an environment variable. `appsettings.json` holds
non-secret defaults only; `appsettings.Production.json` should not exist.

| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ConnectionStrings__Default` | PostgreSQL connection string |
| `POSTGRES_USER`, `POSTGRES_PASSWORD` | Database credentials, consumed by both `db` and `app` |
| `ACCOUNTANT_ADMIN_EMAIL`, `ACCOUNTANT_ADMIN_PASSWORD` | First Accountant Admin, read on first startup only (`App/GeneralAppArchitecture.md` §9) |

**There is no JWT signing key, because there are no JWTs.** Sessions are an `HttpOnly`
`Secure` `SameSite=Strict` cookie (README, locked). Do not add a `JwtOptions` section, a
`SecretKey`, or an `Authorization: Bearer` header anywhere in this application. If you find
one in an older draft, it is stale — delete it.

ASP.NET Core's cookie authentication needs stable data-protection keys, or every restart
invalidates every session. Persist them to a mounted volume or the database. Two replicas of
`app` would also need to share them — a further reason the v1 topology runs one.

Never commit a `.env` file. Keep `.env.example` with empty values in version control.

## 5. Backups and disaster recovery

```bash
#!/bin/bash
# backup.sh — run from cron, daily
set -euo pipefail
DATE=$(date +%Y%m%d_%H%M%S)
FILE="/backups/accountant_app_$DATE.sql.gz"

docker compose exec -T db pg_dump -U "$POSTGRES_USER" accountant_app | gzip > "$FILE"
# Off-machine copy. A backup on the same host is not a backup.
aws s3 cp "$FILE" "s3://your-backup-bucket/"
find /backups -name '*.sql.gz' -mtime +30 -delete
```

Because document bytes live in PostgreSQL (§7), `pg_dump` captures documents too — there is
no second backup target, and no way for the files and the database to drift apart. It also
means the dump grows quickly; watch its size.

**Restore is tested monthly** against a scratch database. An untested backup is a guess.

The 30 days above is the **backup rotation** window, not a data retention policy, and the two
are unrelated. In-application retention is indefinite — nothing is ever hard-deleted
(`01-DomainModel.md` §9.2) — so the live database is the record and old dumps are only there to
recover from a failure. How long *dumps* are kept off-machine depends on the legal minimum for
accounting records in your jurisdiction; raise `-mtime +30` if that minimum is longer.

Do not read this rotation window as licence to add a purge job to the application. No background
work in this system deletes data — see `01-DomainModel.md` §9.2.

## 5a. Outbound email — transport UNDECIDED, delivery model LOCKED

The `Notifications` slice owns email delivery. Two halves, at different stages of decision.

**The delivery model is LOCKED: a transactional outbox drained by one hosted service.**

1. A handler writes the `Notification` row **and** an outbox row in the same transaction, so a
   committed domain event can never lose its email.
2. One `IHostedService` inside the **existing `app` container** polls the outbox and sends. It
   adds no container and no external scheduler. This is the **only** hosted service in the
   system.
3. Sending is never synchronous inside a request. An SMTP or API timeout must not turn a
   successful user action into a `500`.
4. Attempts, failures, and the last error are recorded on the outbox row, with capped
   exponential backoff and a maximum attempt count after which the row is marked
   `Abandoned` — never retried forever, never silently dropped.

**The transport is NOT decided.** The operator expects to use Twilio, which has a .NET library,
but that is not settled. So:

- Everything above the transport is built against **`IEmailSender`**, an interface in the
  `Notifications` slice with one method. Nothing outside that slice knows how mail is sent.
- Until the provider is chosen, the registered implementation is a **logging sender** that
  records the message and returns success, plus a `Notifications:Email:Enabled` flag.
- No provider SDK is added to the project, no API key appears in configuration, and no secret is
  created, until the decision is made.
- **Flag when you reach it:** "Twilio" is ambiguous — Twilio's own API is SMS and verification,
  while its email product is SendGrid, and the two need different libraries, different secrets,
  and different failure handling. Ask which one before writing an implementation. If the answer
  is SMS rather than email, that is a larger change: `01-DomainModel.md` §7 specifies an *email*
  delivery state, and a phone number is not currently stored for a UserAccount.

## 6. Monitoring

Track and alert on:

- API response time — alert if p95 > 1s
- Authentication failure rate — a spike is a credential-stuffing attempt, and each failure is
  already an audit event
- Audit log write failures — `Audit` is fire-and-forget from callers, so a silent failure
  destroys the record with nothing else breaking. This is the one metric that must page.
- PostgreSQL disk usage — alert at 80%. Documents live in the database, so this fills faster
  than it would otherwise.
- Certificate expiry — Caddy renews automatically, but alert if fewer than 14 days remain.

## 7. Document storage

**Document bytes live in PostgreSQL** (README, locked), reached only through the `Documents`
slice's storage interface. Nothing outside that slice knows the mechanism — see
`01-DomainModel.md` §6.

Why the database rather than a volume or object storage:

- One backup covers documents and metadata, and they cannot drift out of sync.
- A row and its bytes are written in one transaction, so there is no orphaned-file cleanup
  job and no half-uploaded file with a database row pointing at nothing.
- Access control is already in the database; a filesystem path is a second thing to secure.

The cost: larger dumps and more database I/O. Acceptable at one-Office scale. Because the
access path is an interface, moving to object storage later touches only the `Documents`
slice.

Store the bytes in a `BYTEA` column, not a large object — `BYTEA` is transactional, comes
back with an ordinary query, and is included in `pg_dump` without extra flags.

### Upload hygiene

- **A content-type allow-list, validated against the actual leading bytes**, not the declared
  `Content-Type` header and not the file extension. Both are attacker-controlled.
- **A maximum file size**, enforced before the body is buffered.
- **Downloads are always `Content-Disposition: attachment`**, never `inline`, and always with
  `X-Content-Type-Options: nosniff`. An HTML or SVG file served inline from the app's own
  origin runs scripts with the session cookie available.
- Serve the stored original filename, sanitised — never a client-supplied path.

**There is no virus scanning and no scan state.** Deliberate, not an omission. Do not add a
`ScanState` field, a quarantine status, or a "pending scan" state to the `Document` entity.
The allow-list and the size cap are the defence.

## 8. Out of scope for v1

Named here so a builder does not add them speculatively:

- **Kubernetes.** `docker compose` on one host is the v1 target. Revisit at operational limits.
- **Horizontal scaling of `app`.** One replica. More than one requires shared data-protection
  keys and a distributed session story that v1 does not need.
- **CI/CD.** Build and deploy manually until the deployment target is stable.
- **A CDN.** The SPA is served by the `app` container behind Caddy.
