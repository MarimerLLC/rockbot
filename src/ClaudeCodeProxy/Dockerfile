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

# Install the Claude Code CLI so the token refresh scheduler can run
# `claude --version` daily to keep the OAuth credentials fresh.
# The native installer places the binary at ~/.local/bin/claude.
RUN apt-get update && apt-get install -y curl ca-certificates --no-install-recommends \
    && curl -fsSL https://claude.ai/install.sh | bash \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Native installer puts the binary at ~/.local/bin — add to PATH
ENV PATH="/root/.local/bin:${PATH}"

COPY package.json ./
RUN npm install --omit=dev

COPY --from=build /app/dist ./dist

EXPOSE 8080

# Mount the Claude Code credentials directory from a writable PVC.
# Must be a PVC (not a k8s Secret) so the CLI can write renewed tokens back.
# Alternatively set ANTHROPIC_API_KEY to skip OAuth entirely.
VOLUME ["/root/.claude"]

ENTRYPOINT ["node", "dist/index.js"]
