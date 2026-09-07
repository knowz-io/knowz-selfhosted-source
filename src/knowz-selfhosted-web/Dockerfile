# Stage 1: Build
# Base images pinned by digest (multi-arch manifest-list digests, supply-chain integrity).
# Refresh: docker buildx imagetools inspect <image>:<tag>  (update tag + digest together).
FROM node:22-alpine@sha256:16e22a550f3863206a3f701448c45f7912c6896a62de43add43bb9c86130c3e2 AS builder

WORKDIR /app

# Copy the manifest AND the lockfile. Lockfile v3 records every platform-specific optional
# package, so `npm ci` selects the right linux/alpine binaries deterministically. A bare
# `npm install` from package.json alone re-resolves the whole graph on every build and
# broke the v0.16.0 cut (npm 10.9.8: "Cannot read properties of null (reading 'edgesOut')").
COPY package.json package-lock.json ./

# Deterministic install from the lockfile (fails loudly if package.json and the lock drift)
RUN npm ci --no-audit --no-fund

# Copy source files (excluding node_modules via .dockerignore)
COPY . .

# Build argument for API URL (empty = relative paths, proxied by nginx)
ARG VITE_API_URL=
ENV VITE_API_URL=${VITE_API_URL}

# Build the application
RUN npm run build

# Stage 2: Serve with nginx
FROM nginx:alpine@sha256:54f2a904c251d5a34adf545a72d32515a15e08418dae0266e23be2e18c66fefa

# Default API upstream (Docker Compose: api:8080, Container Apps: override via env var)
ENV API_UPSTREAM=api:8080
# Protocol for upstream (http for Docker Compose, https for Container Apps with external FQDN)
ENV API_PROTOCOL=http

# Write nginx config template (envsubst processes ${API_UPSTREAM} and ${API_PROTOCOL} on container start)
COPY <<'EOF' /etc/nginx/templates/default.conf.template
server {
    listen 8080;
    server_name localhost;
    root /usr/share/nginx/html;
    index index.html;
    # frame-ancestors is ignored in HTML meta CSP; enforce it on document responses.
    add_header Content-Security-Policy "frame-ancestors 'none'" always;

    # Allow attachments up to 500 MB (matches the API's MultipartBodyLengthLimit).
    # nginx default is 1M, which rejects any image or document larger than 1 MB with 413.
    client_max_body_size 500M;
    client_body_timeout 600s;
    client_header_timeout 600s;

    # DNS resolver for dynamic upstreams (Container Apps internal DNS)
    resolver 127.0.0.11 valid=30s;

    # API reverse proxy to backend container
    location /api/ {
        set $api_upstream ${API_UPSTREAM};
        proxy_pass ${API_PROTOCOL}://$api_upstream;
        proxy_set_header Host ${API_UPSTREAM};
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 600s;
        proxy_send_timeout 600s;
        proxy_connect_timeout 60s;
        # Stream uploads directly to the API instead of buffering the whole 500 MB body first.
        proxy_request_buffering off;
        proxy_ssl_server_name on;
    }

    # Health check proxy
    location /healthz {
        set $api_upstream ${API_UPSTREAM};
        proxy_pass ${API_PROTOCOL}://$api_upstream/healthz;
        proxy_set_header Host ${API_UPSTREAM};
        proxy_ssl_server_name on;
    }

    # SPA routing - serve index.html for all non-file routes
    location / {
        try_files $uri $uri/ /index.html;
    }

    # Cache static assets
    location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg|woff|woff2)$ {
        expires 1y;
        add_header Cache-Control "public, immutable";
    }

    # Gzip compression
    gzip on;
    gzip_types text/plain text/css application/json application/javascript text/xml application/xml application/xml+rss text/javascript;
}
EOF

# Copy built assets from builder
COPY --from=builder /app/dist /usr/share/nginx/html

EXPOSE 8080

CMD ["nginx", "-g", "daemon off;"]
