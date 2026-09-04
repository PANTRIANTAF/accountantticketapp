# AccountantApp

AccountantApp is a .NET API, PostgreSQL database, and Vite frontend. Development runs all three services in Docker.

## Prerequisites

- Docker Engine
- Docker Compose
- Visual Studio Code with the C# extension (`ms-dotnettools.csharp`) for API debugging

This workspace currently uses the legacy Compose v1 command, `docker-compose`. If Docker Compose v2 is installed instead, replace `docker-compose` with `docker compose` in the commands below.

## Start the development app

Start the database, API with `dotnet watch`, and Vite dev server:

```bash
docker-compose up -d --build
```

Open http://localhost:5173. The Vite server proxies `/api` requests to the API, so always use this URL in development rather than opening the API port directly.

The services are exposed on these local addresses:

| Service | Address |
| --- | --- |
| Frontend | http://localhost:5173 |
| API | http://localhost:5131 |
| PostgreSQL | `127.0.0.1:15432` |

Verify the stack after startup:

```bash
docker-compose ps
curl -sS -o /dev/null -w 'UI %{http_code}\n' http://localhost:5173/
curl -sS -o /dev/null -w 'API %{http_code}\n' http://localhost:5131/api/auth/me
```

A `UI 200` and `API 401` are expected. The API endpoint requires an authenticated session, so `401` confirms it is responding.

Useful commands:

```bash
docker-compose logs -f api
docker-compose down
docker-compose down -v
```

`down` preserves database and data-protection-key volumes. `down -v` deletes them, including all local data and the seeded account.

## Debug the API and frontend

The normal development API uses `dotnet watch`, which is convenient for edits but unsuitable for a stable debugger attachment. The debug override builds the API once and runs its assembly as a single process with `vsdbg` installed.

In VS Code, select **Debug in Docker (API + UI)** and press `F5`. This starts the debug stack, attaches the C# debugger to the `api` container, and launches Chrome at http://localhost:5173. Breakpoints work in both C# and TypeScript/TSX files.

To start the debug stack from the terminal:

```bash
docker-compose -f docker-compose.yml -f docker-compose.debug.yml up -d --build
```

After editing C# while debugging, rebuild the API process:

```bash
docker-compose -f docker-compose.yml -f docker-compose.debug.yml restart api
```

The Vite frontend retains hot reload. While execution is paused at a breakpoint, the API health check can temporarily report `unhealthy`; it returns to healthy when execution continues.

## Migrations

Database migrations apply automatically every time the API starts. `Program.cs` runs the SQL migration runner before database seeding.

Migration scripts live under:

```text
AccountantApp.Api/Slices/<Slice>/Infrastructure/Migrations/
```

To add a migration:

1. Create a `.sql` file in the owning slice's `Infrastructure/Migrations` directory.
2. Name it with a globally ordered prefix, for example `20260904_001_AddExampleColumn.sql`.
3. Restart the API or start the stack. The runner discovers migration scripts recursively, orders them by the `YYYYMMDD_###` prefix and then slice-relative path, applies each script in a transaction, and records it in `schema_versions`.

Do not modify a script that has already been applied to a shared database. Add a new migration instead.

Inspect applied migrations:

```bash
docker-compose exec db psql -U postgres -d accountant_app -c \
  'SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;'
```

For a local development reset that reruns every migration:

```bash
docker-compose down -v
docker-compose up -d --build
```

## First admin seeding

After migrations, the API runs `DatabaseSeeder`. If no account with the `AccountantAdmin` role exists, it creates the first administrator from these configuration values:

```text
Seeding__FirstAdminEmail
Seeding__FirstAdminPassword
```

The development Compose file sets them to:

| Setting | Development value |
| --- | --- |
| Email | `admin@accountantapp.local` |
| Password | `development-admin-password` |

The seeded account has the `AccountantAdmin` role, is `Active`, and must change its password after its first login. Seeding is idempotent: it does not create a second administrator or reset an existing administrator's password.

To seed the first admin again in local development, delete the database volume and start the stack:

```bash
docker-compose down -v
docker-compose up -d --build
```

Confirm the account row without reading its password hash:

```bash
docker-compose exec db psql -U postgres -d accountant_app -c \
  'SELECT login_email, display_name, role, status, must_change_password, created_at FROM user_accounts ORDER BY created_at;'
```
