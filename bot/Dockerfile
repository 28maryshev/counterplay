# Двухэтапная сборка: better-sqlite3 — нативный модуль, готового бинарника под
# node в slim-образе может не быть, поэтому первый этап компилирует (python3 +
# g++), финальный образ остаётся тонким, без тулчейна.
FROM node:20-slim AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 make g++ \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev

FROM node:20-slim
WORKDIR /app
COPY --from=build /app/node_modules ./node_modules
COPY . .
CMD ["node", "index.js"]
