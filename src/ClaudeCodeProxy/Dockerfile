FROM node:20-slim AS build
WORKDIR /app

COPY package.json ./
RUN npm install

COPY tsconfig.json ./
COPY src/ ./src/
RUN npm run build

# ── Runtime ───────────────────────────────────────────────────────────────────
FROM node:20-slim AS final
WORKDIR /app

ENV NODE_ENV=production
ENV PORT=8080

COPY package.json ./
RUN npm install --omit=dev

COPY --from=build /app/dist ./dist

EXPOSE 8080

# Mount ~/.claude/ at /root/.claude in the container to use Max subscription OAuth.
# Alternatively set ANTHROPIC_API_KEY for Console API key auth.
VOLUME ["/root/.claude"]

ENTRYPOINT ["node", "dist/index.js"]
