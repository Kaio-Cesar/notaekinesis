# Build:  docker build -f deploy/Dockerfile -t notaekinesis .
FROM oven/bun:1.4 AS build
WORKDIR /app
COPY package.json bun.lock bunfig.toml turbo.json ./
COPY server/package.json server/
COPY web/package.json web/
RUN bun install --frozen-lockfile
COPY server server
COPY web web
RUN bun --cwd web run build

FROM oven/bun:1.4-slim
WORKDIR /app
ENV NODE_ENV=production \
    PORT=3000 \
    DATA_DIR=/data \
    WEB_DIST=/app/web/dist
COPY --from=build /app/package.json /app/bun.lock /app/bunfig.toml ./
COPY --from=build /app/server/package.json server/
COPY --from=build /app/web/package.json web/
RUN bun install --frozen-lockfile --production --filter @notaekinesis/server
COPY --from=build /app/server/src server/src
COPY --from=build /app/server/drizzle server/drizzle
COPY --from=build /app/web/dist web/dist
VOLUME /data
EXPOSE 3000
USER bun
HEALTHCHECK --interval=30s --timeout=3s CMD bun -e "fetch('http://localhost:3000/healthz').then(r => process.exit(r.ok ? 0 : 1)).catch(() => process.exit(1))"
CMD ["bun", "server/src/index.ts"]
