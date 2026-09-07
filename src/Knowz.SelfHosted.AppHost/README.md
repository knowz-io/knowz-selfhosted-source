# Knowz Self-Hosted AppHost (Aspire)

Dedicated .NET Aspire orchestrator for the self-hosted scenario. Starts everything you need for local development with a single command.

## What it starts

| Resource | Description | Port |
|----------|-------------|------|
| `pg` | Postgres 16 + pgvector container with `McpDb` database | (dynamic) |
| `selfhosted-api` | Knowz Self-Hosted API (ASP.NET Core) | 5000 |
| `selfhosted-web` | Knowz Self-Hosted Web Client (React + Vite) | 5173 |
| Aspire Dashboard | Traces, logs, metrics | 17200 |

## Dev Modes

Three ways to run, matching `INFRA_MODE` on the main Knowz platform:

| Mode | SQL | Storage | How to run |
|------|-----|---------|------------|
| **local** (default) | Postgres 16 + pgvector (`pgvector/pgvector:pg16`) | Local FS | `--launch-profile local` |
| **cloud** | Azure (rg-knowz-sh) | Azure Blob | `--launch-profile cloud` |
| **UI only** | — | — | `npm run dev:cloud` (no Aspire) |

### One-time setup for cloud/UI-only modes

```bash
# From selfhosted/ directory — pulls all secrets from rg-knowz-sh Key Vault
./scripts/setup-sh-dev.ps1
```

### Start in cloud mode (no Docker, real data)

```bash
cd src/knowz-selfhosted-web && npm install && cd ../..
dotnet run --project src/Knowz.SelfHosted.AppHost --launch-profile cloud
```

### Start in local mode (SQL container)

```bash
cd src/knowz-selfhosted-web && npm install && cd ../..
dotnet run --project src/Knowz.SelfHosted.AppHost --launch-profile local
```

### UI only — web client proxies to deployed Container App

No Aspire, no local API, no Docker. Web client at http://localhost:5173 talks directly to a deployed Container Apps API. Useful for UX work against a real backend without running anything locally.

```bash
cd src/knowz-selfhosted-web
npm run dev:cloud
```

The target endpoint comes from `VITE_API_URL` in `.env.local` (or the `dev:cloud` script's default). Point it at your own deployment's API URL — the Container App URL printed by `selfhosted-deploy.ps1` works directly.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/setup-tooling): `dotnet workload install aspire`
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) — only needed for `local` mode
- [Node.js 22+](https://nodejs.org/) (for the web client)

## AI Services Configuration

The selfhosted API uses a **three-tier fallback** for AI services:

| Tier | Name | What it provides | Config needed |
|------|------|-----------------|---------------|
| 1 | **Knowz Platform Proxy** | AI via platform API, local keyword search | 3 values |
| 2 | **Direct Azure** | Full AI + vector search via your own Azure resources | 7 values |
| 3 | **NoOp** (default) | AI disabled, auth/admin/CRUD still work | None |

### Tier 1: Knowz Platform Proxy (simplest)

Delegates AI operations (completions, embeddings, summarization, enrichment) to the Knowz Platform API. Search uses local keyword matching. Best for quick setup without managing Azure resources.

```bash
cd src/Knowz.SelfHosted.AppHost
dotnet user-secrets set "KnowzPlatform:Enabled" "true"
dotnet user-secrets set "KnowzPlatform:BaseUrl" "https://api.dev.knowz.io"
dotnet user-secrets set "KnowzPlatform:ApiKey" "ukz_your_api_key"
```

### Tier 2: Direct Azure OpenAI + Azure AI Search (full-featured)

Uses your own Azure resources for AI and vector search. Provides the best search quality (hybrid vector + keyword) and full enrichment pipeline.

```bash
cd src/Knowz.SelfHosted.AppHost

# Azure OpenAI (completions, embeddings, summarization)
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://your-openai.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey" "your-key"
dotnet user-secrets set "AzureOpenAI:DeploymentName" "gpt-5.4"
dotnet user-secrets set "AzureOpenAI:EmbeddingDeploymentName" "text-embedding-3-large"

# Embedding model + dim (required when using Azure AI Search).
# Must match deployed model: 3072 for -3-large (default), 1536 for -3-small / ada-002.
dotnet user-secrets set "Embedding:ModelName" "text-embedding-3-large"
dotnet user-secrets set "Embedding:Dimensions" "3072"

# Azure AI Search (hybrid vector + keyword search)
dotnet user-secrets set "AzureAISearch:Endpoint" "https://your-search.search.windows.net/"
dotnet user-secrets set "AzureAISearch:ApiKey" "your-key"
dotnet user-secrets set "AzureAISearch:IndexName" "knowledge"
```

### Tier comparison

| Feature | Tier 1 (Platform) | Tier 2 (Azure) | Tier 3 (NoOp) |
|---------|-------------------|----------------|----------------|
| Completions/Chat | Via platform API | Direct Azure OpenAI | Disabled |
| Embeddings | Via platform API | Direct Azure OpenAI | Disabled |
| Summarization | Via platform API | Local with Azure OpenAI | Disabled |
| Entity extraction | Via platform API | Local with Azure OpenAI | Disabled |
| Enrichment pipeline | Full (via platform) | Full (direct) | Disabled |
| Search | Keyword (SQL LIKE) | **Vector + keyword hybrid** | Disabled |
| Setup complexity | 3 config values | 7 config values | None |

### Alternative: appsettings.Local.json

Instead of user-secrets, you can create `appsettings.Local.json` in the API project (gitignored):

```bash
cp src/Knowz.SelfHosted.API/appsettings.Local.json.example \
   src/Knowz.SelfHosted.API/appsettings.Local.json
# Edit with your credentials
```

Both approaches work. User-secrets are managed by the AppHost and injected as environment variables (highest priority). `appsettings.Local.json` is read directly by the API project.

### Verify secrets are set

```bash
dotnet user-secrets list --project src/Knowz.SelfHosted.AppHost
```

## Setup

### 1. Install npm dependencies for the web client

```bash
cd src/knowz-selfhosted-web
npm install
cd ../..
```

### 2. (Optional) Configure AI credentials

See [AI Services Configuration](#ai-services-configuration) above.

## Run

```bash
dotnet run --project src/Knowz.SelfHosted.AppHost --launch-profile local
```

This starts:
1. **Postgres + pgvector container** -- with `McpDb` database (auto-created by Aspire)
2. **Self-Hosted API** -- applies EF Core migrations automatically (`Database__AutoMigrate=true`), seeds SuperAdmin user. AI credentials injected only if configured.
3. **Self-Hosted Web Client** -- Vite dev server with hot reload, proxies `/api` requests to the API
4. **Aspire Dashboard** -- opens in browser automatically (traces, logs, metrics for all resources)

## How it works

- **Postgres**: Aspire starts `pgvector/pgvector:pg16` and creates `McpDb`. The connection string is injected into the API automatically via `ConnectionStrings:McpDb`.
- **Auto-migration**: On startup, the API runs `Database.MigrateAsync()` to apply all pending EF Core migrations. If migration fails (e.g., Postgres still starting), it logs a warning and continues.
- **SuperAdmin seeding**: After migration, the API seeds the SuperAdmin user from `appsettings.json` config.
- **AI services**: The AppHost reads AI config from user-secrets and conditionally injects environment variables into the API. The API's three-tier fallback (Platform > Azure > NoOp) determines which services are registered. OpenAI and Search can be configured independently.
- **Web client**: The Vite dev server proxies `/api` requests to `localhost:5000` (the API's fixed port).

## Modes

### Local Mode (default, recommended for development)

Spins up a Postgres 16 + pgvector container. AI features require either Platform proxy or Azure credentials.

```bash
dotnet run --project src/Knowz.SelfHosted.AppHost --launch-profile local
```

### Azure Mode (requires deployed Azure resources)

No containers -- reads all connection strings from config. Use after running the [deploy script](../../README.md#azure-deployment).

```bash
dotnet run --project src/Knowz.SelfHosted.AppHost
```

## Ports

| Service | Port | Purpose |
|---------|------|---------|
| Aspire Dashboard (HTTPS) | 17200 | Resource monitoring |
| Aspire Dashboard (HTTP) | 17201 | Resource monitoring |
| Self-Hosted API | 5000 | REST API (fixed port for Vite proxy) |
| Self-Hosted Web | 5173 | Vite dev server |
| OTLP Endpoint | 21200/21201 | OpenTelemetry collector |

Port range 172xx avoids conflicts with the full platform AppHost (151xx/170xx).

## Data Portability (Import/Export)

Knowz Self-Hosted includes a full data portability system for migrating data between hosting models — self-hosted to self-hosted, or between the Knowz platform (cloud) and self-hosted — with zero data loss.

### Portability API Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/portability/export` | Export all tenant data as JSON |
| POST | `/api/portability/import/validate` | Dry-run validation (preview before import) |
| POST | `/api/portability/import?strategy=skip` | Execute import with conflict strategy |
| GET | `/api/portability/schema` | Check schema version and compatibility |

All endpoints require authentication.

### Quick Start

```bash
# Export your data
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/portability/export > backup.json

# Validate an import (dry-run, no changes made)
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d @backup.json \
  http://localhost:5000/api/portability/import/validate

# Import with conflict handling
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d @backup.json \
  "http://localhost:5000/api/portability/import?strategy=overwrite"

# Check schema compatibility
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/portability/schema
```

### Conflict Strategies

| Strategy | Behavior |
|----------|----------|
| `skip` (default) | Keep existing data, skip conflicts |
| `overwrite` | Replace existing data with imported data |
| `merge` | Fill in missing fields only, keep existing non-null values |

### Platform Round-Trip

When migrating from the Knowz cloud platform to self-hosted and back, all platform-specific data (AI enrichments, entity extraction results, perspectives, temporal contexts, etc.) is preserved transparently via the `PlatformData` mechanism. Self-hosted doesn't need to understand these fields — they're stored opaquely and restored on re-export.

For full documentation, see [Data Portability Guide](../../docs/DATA_PORTABILITY.md).

## Standalone mode (without Aspire)

The API can still be run standalone without Aspire:

```bash
dotnet run --project src/Knowz.SelfHosted.API
```

In this mode, configure the database connection string in `appsettings.Local.json` (gitignored) and auto-migration is not triggered (no `Database__AutoMigrate` env var).

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "Aspire workload not installed" | Run `dotnet workload install aspire` |
| SQL container fails to start | Ensure Docker Desktop is running |
| Migration fails on startup | SQL may still be initializing — the API logs a warning and retries on next restart |
| Search/Q&A returns "not configured" | This is expected without AI credentials. See [Setup step 2](#2-optional-configure-ai-credentials-via-user-secrets) to enable AI features |
| Web client not starting | Run `npm install` in `src/knowz-selfhosted-web/` first |
| Port conflict on 5000 | Stop any other process using port 5000, or modify `.WithHttpEndpoint(port: 5000)` in Program.cs |
