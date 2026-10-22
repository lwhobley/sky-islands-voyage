# syntax=docker/dockerfile:1
# Sky Islands service (IAP verification, push worker, admin API).
#   docker build -t sky-islands-server server/
#   docker run --env-file server/.env -p 3000:3000 sky-islands-server

FROM node:22-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts
COPY tsconfig.json tsconfig.build.json ./
COPY src ./src
RUN npm run build

FROM node:22-slim AS prod-deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force

FROM node:22-slim
ENV NODE_ENV=production
WORKDIR /app
COPY --from=prod-deps /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY package.json ./
# Secrets (Apple .p8, Google/Firebase service accounts) are mounted, never baked in:
#   -v /path/to/secrets:/secrets:ro  with APPLE_PRIVATE_KEY_PATH=/secrets/... etc.
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.PORT||3000)+'/health').then(r=>process.exit(r.ok?0:1),()=>process.exit(1))"
CMD ["node", "dist/main.js"]
