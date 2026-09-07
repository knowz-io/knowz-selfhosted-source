# Quickstart Guide

Install Knowz Self-Hosted with the Knowz CLI. `knowz up` pulls published GHCR **release images** and starts the web UI. You do not clone a repository or build from source.

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker Engine + Compose plugin)
- [Node.js 22+](https://nodejs.org/)
- At least 1 GB of available RAM for the Postgres + API containers

## Step 1: Start with the CLI

```bash
npx @knowzai/cli@latest up
```

Here Forever (same CLI, branded chrome — not a fork):

```bash
npx hereforever up
```

Or `knowz --hereforever up`. See [`docs/HEREFOREVER_CLI.md`](../../docs/HEREFOREVER_CLI.md).

Or install once, then run:

```bash
npm i -g @knowzai/cli
knowz up
```

This pulls the public GHCR **release images** `ghcr.io/knowz-io/knowz-selfhosted-api`, `ghcr.io/knowz-io/knowz-selfhosted-web`, and `ghcr.io/knowz-io/knowz-selfhosted-mcp`, writes runtime files under `~/.knowz/selfhosted/`, generates secrets, and starts the stack. Default edition is self-hosted. No GitHub authentication is required. No source checkout.

The CLI is the guided install path. The [public Apache-2.0 source](https://github.com/knowz-io/knowz-selfhosted-source) is also available for customization; see [Compose from a checkout](#compose-from-a-checkout-operators-only).

## Step 2: Wait for Startup

The CLI waits until the stack is healthy, then prints the bound URLs and the first administrator credentials (also shown on the loopback setup page).

On first run the API will:

1. Wait for Postgres to become healthy (`pg_isready`)
2. Run database migrations automatically
3. Create the SuperAdmin account from the credentials the CLI generated (or that you set)

Default ports if you did not remap them:

| Service | Description | Port |
|---------|-------------|------|
| **db** | PostgreSQL 16 + pgvector (`pgvector/pgvector:pg16`) | 5432 |
| **api** | Knowz API (.NET 10) | 5000 |
| **web** | Web UI (React + nginx) | 3000 |
| **mcp** | MCP Server | 3001 |

`knowz up` (0.16.0) still publishes Postgres on loopback `127.0.0.1:5432`. Source compose in this tree does **not** publish a database host port.

## Step 3: Log In

Open the web UI the CLI printed (default [http://localhost:3000](http://localhost:3000)) and log in with the administrator username and password from the setup summary.

## First Steps After Login

1. **Create a vault** -- Vaults are containers for organizing your knowledge
2. **Add knowledge** -- Create knowledge items manually or upload files
3. **Try search** -- Search across your knowledge base (full-text search works without AI; semantic search requires an embedding model)
4. **Generate an API key** -- Go to Settings to create a per-user API key for programmatic access

## Enabling AI Features

Knowz works without AI services for basic knowledge management. To enable AI-powered search, chat, and automatic enrichment, choose one of two approaches during `knowz up` or by editing the runtime `.env` under `~/.knowz/selfhosted/`:

### Option 1: Connect to Knowz Cloud (simplest)

No Azure subscription needed -- just an API key from your Knowz Cloud account:

```bash
KNOWZ_PLATFORM_ENABLED=true
KNOWZ_PLATFORM_URL=https://api.knowz.io
KNOWZ_PLATFORM_APIKEY=ukz_your_api_key
```

AI operations (chat, summarization, embeddings, enrichment) are answered by Knowz Cloud. Search stays local and uses keyword matching. Nothing leaves this instance until you set these values.

### Option 2: Your own Azure OpenAI resources

Uses your own Azure resources for the best search quality (hybrid vector + keyword):

1. Set up an [Azure OpenAI](https://azure.microsoft.com/en-us/products/ai-services/openai-service) resource
2. Set up an [Azure AI Search](https://azure.microsoft.com/en-us/products/ai-services/ai-search) resource
3. Add the credentials (see [Configuration Reference](CONFIGURATION.md))

### After configuring either option:

```bash
knowz up
```

## Stopping and Restarting

```bash
# Stop all services (data is preserved in Docker volumes)
knowz down

# Start again
knowz up
```

## Upgrading

```bash
knowz up
```

The CLI pulls the current complete public **release images**. The API automatically applies any new database migrations on startup.

## Compose from a checkout (operators only)

Clone the public Apache-2.0 source when you want to customize or build the stack. The guided CLI install above is easier for a standard installation.

```bash
git clone https://github.com/knowz-io/knowz-selfhosted-source.git
cd knowz-selfhosted-source
```

> **Easier path:** Run the interactive setup wizard instead of hand-editing `.env`:
> ```bash
> dotnet run --project src/Knowz.SelfHosted.Setup
> ```
> It prompts you through run mode, AI tier, storage, and credentials, generates strong random secrets, and writes the appropriate config file (`.env` for Docker Compose, user-secrets for Aspire, `appsettings.Local.json` for Direct Run). Skip the rest of this step if you use the wizard.

Otherwise, copy the example environment file:

```bash
cp .env.example .env
```

Open `.env` and **set every required value** — no defaults ship, and `docker compose up` fails fast (with a clear error message) if any of these are missing or empty:

| Variable | Constraint | How to generate |
|----------|------------|------------------|
| `POSTGRES_PASSWORD` | ≥8 chars, mixed character classes recommended | `openssl rand -base64 24` |
| `JWT_SECRET` | ≥32 chars, cryptographically random | `openssl rand -base64 48 \| tr -d '/+=\n' \| head -c 64` |
| `ADMIN_USERNAME` | non-empty | your choice — `admin` is fine |
| `ADMIN_PASSWORD` | ≥12 chars; rejected if on weak-password denylist (`changeme`, `password`, `admin`, etc.) | `openssl rand -base64 24` |
| `MCP_SERVICE_KEY` | non-empty; same value on API + MCP containers | `openssl rand -base64 24` |

`.env.example` includes copy-pasteable PowerShell + bash generation commands at the top.

```bash
docker compose up -d
```

Source compose does not publish a database host port (internal `db:5432` only). Swagger is off; set `ENABLE_SWAGGER=true` for local debug. Check that all services are running:

```bash
docker compose ps
```

Log in at [http://localhost:3000](http://localhost:3000) with `ADMIN_USERNAME` / `ADMIN_PASSWORD`. Stop with `docker compose down` (volumes preserved). Upgrade with `docker compose pull && docker compose up -d`.

## Troubleshooting

### CLI install

If `npx @knowzai/cli up` / `knowz up` fails, confirm Docker is running and Node is 22+. Remap occupied ports with `--api-port`, `--web-port`, `--mcp-port`, and `--name`. The CLI prints the bound URLs.

### Port Conflicts

If you are on the operator compose path and ports 3000, 3001, or 5000 are already in use, either stop the conflicting service or change the port mapping in `docker-compose.yml`:

```yaml
ports:
  - "3001:8080"  # Change the left side (host port) to an available port
```

The database does not occupy a host port. A 0.16.0 compose that still maps `127.0.0.1:${DB_PORT:-5432}:5432` is the previous default — safe on loopback, but no longer shipped.

### Postgres Memory

`pgvector/pgvector:pg16` is a light image. If the `db` container exits immediately, check Docker Desktop memory allocation under Settings > Resources. Host port 5432 is not published by default, so a local Postgres install does not collide.

### API Fails to Start

Check the API logs for details:

```bash
docker compose logs api
```

Common issues:
- **Database connection failed** -- The Postgres container may still be starting. The API retries automatically with exponential backoff (up to 10 attempts).
- **Migration error** -- If the database was manually modified, you may need to reset it: `docker compose down -v` (warning: this deletes all data).

### ARM64 (Apple Silicon) Notes

`pgvector/pgvector:pg16` is multi-arch. No Rosetta emulation is required.

### Viewing Logs

```bash
# All services
docker compose logs -f

# Specific service
docker compose logs -f api
```
