# Parameterized Dockerfile for the Corridor.Spa (FieldInsight) container.
# Build context is the REPO ROOT (see docker-compose.yml): compose resolves the
# dockerfile inside the context, so it cannot point at deploy/ from a
# src/Corridor.Spa context. Build args:
#   SRC_DIR                  SPA directory inside the repo, default src/Corridor.Spa
#   VITE_OIDC_AUTHORITY      okta-sim base URL baked into the bundle (browser facing)
#   VITE_PORTAL_API          portal base URL baked into the bundle (browser facing)
#   VITE_OIDC_REDIRECT_URI / VITE_OIDC_POST_LOGOUT_URI  SPA callback URLs
# Defaults match the demo topology (okta-sim 8080, portal 5200, spa 5173), so a
# plain build with no args produces the same bundle as a local vite build. The
# root .dockerignore keeps node_modules and dist out of the context; the
# .dockerignore inside src/Corridor.Spa covers standalone builds that use that
# directory as their context.
FROM node:22-alpine AS build
ARG SRC_DIR=src/Corridor.Spa
ARG VITE_OIDC_AUTHORITY=http://localhost:8080
ARG VITE_PORTAL_API=http://localhost:5200
ARG VITE_OIDC_REDIRECT_URI=http://localhost:5173/callback
ARG VITE_OIDC_POST_LOGOUT_URI=http://localhost:5173/
WORKDIR /app

COPY ${SRC_DIR}/package.json ${SRC_DIR}/package-lock.json ./
RUN npm ci

COPY ${SRC_DIR}/ .
ENV VITE_OIDC_AUTHORITY=${VITE_OIDC_AUTHORITY} \
    VITE_PORTAL_API=${VITE_PORTAL_API} \
    VITE_OIDC_REDIRECT_URI=${VITE_OIDC_REDIRECT_URI} \
    VITE_OIDC_POST_LOGOUT_URI=${VITE_OIDC_POST_LOGOUT_URI}
RUN npm run build

FROM nginx:alpine AS final
# Serve the SPA on the contract port 5173; SPA router fallback to index.html
# and immutable caching for hashed assets. Generated inline so the src tree
# stays free of docker-only config files.
RUN printf 'server {\n\
    listen 5173;\n\
    server_name _;\n\
    root /usr/share/nginx/html;\n\
    index index.html;\n\
    location /assets/ {\n\
        add_header Cache-Control "public, max-age=31536000, immutable";\n\
        try_files $uri =404;\n\
    }\n\
    location / {\n\
        try_files $uri $uri/ /index.html;\n\
    }\n\
}\n' > /etc/nginx/conf.d/default.conf

COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 5173
