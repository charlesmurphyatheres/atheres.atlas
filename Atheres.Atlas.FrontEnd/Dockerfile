# =============================================================
# Atheres.Atlas.FrontEnd — Multi-stage Dockerfile
# Build context: Atheres.Atlas.FrontEnd/
# =============================================================

# ---- Stage 1: Build React app --------------------------------
FROM node:20-alpine AS build
WORKDIR /app

# Install dependencies first (layer cache)
COPY package.json package-lock.json* ./
RUN npm ci --silent

# VITE_ env vars are baked into the bundle at build time
ARG VITE_GOOGLE_MAPS_KEY=""
ENV VITE_GOOGLE_MAPS_KEY=${VITE_GOOGLE_MAPS_KEY}

# Copy source and build
COPY . .
RUN npm run build

# ---- Stage 2: Serve with nginx -------------------------------
FROM nginx:1.27-alpine AS runtime

# Remove default config
RUN rm /etc/nginx/conf.d/default.conf

# Copy built assets and nginx SPA config
COPY --from=build /app/dist              /usr/share/nginx/html
COPY             nginx.conf              /etc/nginx/conf.d/default.conf

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
