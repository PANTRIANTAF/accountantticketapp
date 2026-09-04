# The `app` image: the ASP.NET Core API AND the built React SPA in one container
# (04-Infrastructure.md section 1, LOCKED -- three containers in production, and the SPA is not one
# of them). Build from the REPOSITORY ROOT, not from either project directory:
#
#     docker build -t accountantapp .
#
# This file is production only. Development runs AccountantApp.Api/Dockerfile.dev and
# frontend/Dockerfile.dev instead, because hot reload is worth the extra two images
# (04-Infrastructure.md section 2).

# ---------------------------------------------------------------------------------------------
# 1. The SPA. Its output becomes the API's wwwroot in the final stage.
# ---------------------------------------------------------------------------------------------
#
# node:22-alpine, NOT the node:20-alpine that 04-Infrastructure.md section 3 shows. The installed
# Vite is 8.2.2 and its own package.json declares `engines: { node: "^20.19.0 || >=22.12.0" }`, so
# the `node:20-alpine` tag is only viable while it happens to resolve to 20.19.x -- and Node 20 left
# active support in 2025. The doc predates the dependency lock; the tag there is stale, not a
# constraint. Pinning to 22 also keeps this in the same major as the LTS line the lockfile was
# resolved against.
FROM node:22-alpine AS ui
WORKDIR /ui

# The lockfile is copied WITH package.json and `npm ci` is used rather than `npm install`:
# `ci` fails when the two disagree instead of quietly resolving something newer, which is the whole
# point of building a release image. It also means this layer is cached until a dependency changes.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

# devDependencies are installed on purpose: `npm run build` is `tsc --noEmit && vite build`, and both
# tsc and vite are devDependencies. `npm ci --omit=dev` here fails with "tsc: not found".
COPY frontend/ ./
RUN npm run build

# ---------------------------------------------------------------------------------------------
# 2. The API.
# ---------------------------------------------------------------------------------------------
# Keep this tag, AccountantApp.Api.csproj's <TargetFramework>, and the runtime tag below in
# agreement. A mismatch fails the build, which is the good outcome -- but only if all three move
# together.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src

# csproj first, restore, then the rest: the restore layer survives every source edit.
COPY AccountantApp.Api/AccountantApp.Api.csproj AccountantApp.Api/
RUN dotnet restore AccountantApp.Api/AccountantApp.Api.csproj

COPY AccountantApp.Api/ AccountantApp.Api/
RUN dotnet publish AccountantApp.Api/AccountantApp.Api.csproj -c Release -o /publish --no-restore

# ---------------------------------------------------------------------------------------------
# 3. Runtime.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=api /publish ./
# UseStaticFiles serves this directory; the SPA's own router owns every path that is not /api.
COPY --from=ui /ui/dist ./wwwroot

# The data-protection key ring. IdentityRegistration.AddDataProtection REFUSES TO START when
# DataProtection:KeyPath is blank or not writable (deliberately -- the in-memory fallback works
# perfectly in testing and signs everybody out on every restart in production). So the directory is
# created here and handed to the non-root user BEFORE the USER line: a compose named volume mounted
# at this path inherits the image's ownership for that directory, and without the chown it arrives
# owned by root and the container exits at startup with "is not writable".
#
# MOUNT A VOLUME HERE. 04-Infrastructure.md section 4 requires the keys to persist ("Persist them to
# a mounted volume or the database") but the compose file in section 3 has no volume for them --
# docker-compose.prod.yml in this repository adds `app_keys:/app/keys`, which is that requirement.
RUN mkdir -p /app/keys && chown -R $APP_UID:$APP_UID /app/keys

ENV ASPNETCORE_URLS=http://+:8080 \
    DataProtection__KeyPath=/app/keys

# No ports are published for this container; only caddy publishes anything. EXPOSE documents the
# port for `caddy`'s reverse_proxy and for `docker inspect`.
EXPOSE 8080

# Do not run as root. $APP_UID (1654) is set by the base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "AccountantApp.Api.dll"]
