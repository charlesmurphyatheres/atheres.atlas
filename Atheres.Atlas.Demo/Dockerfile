# =============================================================
# Atheres.Atlas.Demo — Multi-stage Dockerfile
# Build context: Atheres.Atlas.Demo/
# =============================================================

# ---- Stage 1: Build React app --------------------------------
FROM node:20-alpine AS build
WORKDIR /app

COPY package.json package-lock.json* ./
RUN npm install --silent

COPY . .
RUN npm run build

# ---- Stage 2: Serve with nginx -------------------------------
FROM nginx:1.27-alpine AS runtime

RUN rm /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist              /usr/share/nginx/html
COPY             nginx.conf              /etc/nginx/conf.d/default.conf

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
