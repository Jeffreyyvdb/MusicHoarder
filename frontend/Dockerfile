# Multi-stage build for SvelteKit + Bun + adapter-node.
# Build stage uses Bun for fast installs; runtime stage uses Bun to serve
# the adapter-node output via `bun build/index.js`.
#
# The build context is the REPOSITORY ROOT, not frontend/: the /docs pages prerender the
# repository's docs/ Markdown at build time (#lib/docs/content.ts globs ../../../../docs),
# so the image build needs both trees. All COPY paths below are repo-root-relative.

FROM oven/bun:1-alpine AS build
WORKDIR /app/frontend

COPY frontend/package.json frontend/bun.lock ./
RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache \
    bun install --frozen-lockfile

COPY frontend/ ./
COPY docs/ ../docs/

ARG PUBLIC_SITE_URL
ENV PUBLIC_SITE_URL=$PUBLIC_SITE_URL

# Umami analytics (PUBLIC_UMAMI_*) is read at runtime via $env/dynamic/public —
# supplied by the container env (docker-compose / AppHost), not baked in here.

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

COPY --from=build --chown=svelte:svelte /app/frontend/build ./build
COPY --from=build --chown=svelte:svelte /app/frontend/node_modules ./node_modules
COPY --from=build --chown=svelte:svelte /app/frontend/package.json ./package.json

USER svelte

EXPOSE 3000

# Dokploy reads this to gate zero-downtime traffic switching
# (https://docs.dokploy.com/docs/core/applications/zero-downtime).
# Probe $PORT (not a hardcoded port) so it follows the runtime port — Aspire's compose runs the
# server on PORT=8001, not the image default 3000.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS "http://localhost:${PORT:-3000}/api/health" || exit 1

CMD ["bun", "build/index.js"]
