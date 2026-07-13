# node:20-slim (Debian/glibc): у better-sqlite3 есть готовые бинарники,
# компилятор в образе не нужен. Alpine/musl потребовал бы build-deps.
FROM node:20-slim

WORKDIR /app

COPY package*.json ./
RUN npm ci --omit=dev

COPY . .

CMD ["node", "index.js"]
