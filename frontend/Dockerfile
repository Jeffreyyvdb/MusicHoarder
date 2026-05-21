# Multi-stage build for SvelteKit + Bun + adapter-node.
# Build stage uses Bun for fast installs; runtime stage uses Bun to serve
# the adapter-node output via `bun build/index.js`.

FROM oven/bun:1-alpine AS build
WORKDIR /app

COPY package.json bun.lock ./
RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache \
    bun install --frozen-lockfile

COPY . .

ARG PUBLIC_SITE_URL
ENV PUBLIC_SITE_URL=$PUBLIC_SITE_URL
ARG PUBLIC_UMAMI_SRC
ENV PUBLIC_UMAMI_SRC=$PUBLIC_UMAMI_SRC
ARG PUBLIC_UMAMI_RECORDER_SRC
ENV PUBLIC_UMAMI_RECORDER_SRC=$PUBLIC_UMAMI_RECORDER_SRC
ARG PUBLIC_UMAMI_WEBSITE_ID
ENV PUBLIC_UMAMI_WEBSITE_ID=$PUBLIC_UMAMI_WEBSITE_ID
ARG PUBLIC_UMAMI_HOST_URL
ENV PUBLIC_UMAMI_HOST_URL=$PUBLIC_UMAMI_HOST_URL

ENV NODE_ENV=production
RUN bun run build
RUN bun install --frozen-lockfile --production

# ─────────────────────────────────────────────────────────
FROM oven/bun:1-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3000
ENV HOST=0.0.0.0

RUN apk add --no-cache curl \
    && addgroup --system --gid 1001 svelte \
    && adduser --system --uid 1001 svelte

COPY --from=build --chown=svelte:svelte /app/build ./build
COPY --from=build --chown=svelte:svelte /app/node_modules ./node_modules
COPY --from=build --chown=svelte:svelte /app/package.json ./package.json

USER svelte

EXPOSE 3000

# Dokploy reads this to gate zero-downtime traffic switching
# (https://docs.dokploy.com/docs/core/applications/zero-downtime).
# Probe $PORT (not a hardcoded port) so it follows the runtime port — Aspire's compose runs the
# server on PORT=8001, not the image default 3000.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS "http://localhost:${PORT:-3000}/api/health" || exit 1

CMD ["bun", "build/index.js"]
