# Caddy with the rate-limit module compiled in.
#
# 04-Infrastructure.md section 3 specifies `image: caddy:2-alpine` AND a `rate_limit` directive in the
# Caddyfile. Those two are incompatible: rate limiting is not in Caddy's standard build, and Caddy
# treats an unknown directive as a fatal config error rather than ignoring it -- so the stock image
# plus that Caddyfile gives a proxy that never starts and an application that is unreachable, with the
# reason only in `docker compose logs caddy`.
#
# The document names the alternative: "if unavailable, do it in the API instead". Compiling the module
# is the cheaper of the two, because the API has no rate limiter and the README makes the limit
# mandatory rather than optional.
#
# This is the one image here that is slow to build (it compiles Caddy from source, roughly a minute)
# and it is cached until the pinned versions below change.
FROM caddy:2-builder-alpine AS builder

# Pinned, not @latest: an unpinned module means `docker build` produces a different proxy on different
# days, and this is the component that terminates TLS.
RUN xcaddy build \
    --with github.com/mholt/caddy-ratelimit@v0.1.0

FROM caddy:2-alpine

# Replace the standard binary with the one built above. Everything else about the official image --
# the entrypoint, /etc/caddy/Caddyfile as the config path, /data and /config as the volume mounts --
# is unchanged, so docker-compose.prod.yml treats it exactly like the upstream image.
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
