# Knowz Self-Hosted

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

Knowz Self-Hosted runs the Knowz knowledge model on your own host: your database, your AI keys, and a Model Context Protocol (MCP) server so the AI tools you already use can read and write it.

## Install (customers)

These instructions target the **Knowz CLI 0.5.0 preview**. The CLI version and selfhosted image version are separate. `knowz up` opens local browser setup, pulls published GHCR **release images**, and starts the web UI. No source checkout or build is required.

```bash
npx @knowzai/cli@0.5.0 up
```

Or install once, then run:

```bash
npm i -g @knowzai/cli@0.5.0
knowz up
```

Node-free native preview installers and their checksums are listed in [Knowz CLI releases](https://github.com/knowz-io/knowz-cli/releases). The 0.5.0 native preview is unsigned and not notarized; use the status recorded for your exact artifact. The npm commands above require Node.js.

Requires [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker Engine + Compose) and [Node.js 22+](https://nodejs.org/). Default edition is self-hosted. The CLI generates secrets, opens a loopback setup page, and pulls the current public image triple (`ghcr.io/knowz-io/knowz-selfhosted-api`, `knowz-selfhosted-web`, `knowz-selfhosted-mcp`). No GitHub login.

Setup creates the first administrator (`admin`). If you leave the password blank, use **Show password** or **Copy password** in the setup result to save the generated temporary password; it is shown once. Sign in at the Web URL and change that temporary password before continuing. Return to **Connect CLI** with your current password to finish CLI access. A healthy stack can say **Finish signing in** until this completes. Further accounts are created by an administrator. Stop with `knowz down` (data volumes are preserved).

Use the URLs shown by setup; these are defaults when no ports were remapped:

| Service | URL |
|---------|-----|
| Web UI | [http://localhost:3000](http://localhost:3000) |
| API | [http://localhost:5000](http://localhost:5000) |
| MCP Server | [http://localhost:3001](http://localhost:3001) |

Swagger is off by default; set `ENABLE_SWAGGER=true` (or `SelfHosted__EnableSwagger=true`) to serve `/swagger` for local debug. The CLI-managed stack and source compose do not publish Postgres on the host — see [Configuration](docs/CONFIGURATION.md#connection-strings). `AllowedHosts` is localhost plus loopback (compose also adds the container DNS name), not `*`; set `ALLOWED_HOSTS` when using a public hostname.

Operators and contributors who need to customize or build locally: see [Contributing](CONTRIBUTING.md) and [Compose from a checkout](#compose-from-a-checkout-operators-only).

## What this edition is

Knowz Self-Hosted is the limited edition of Knowz: the same knowledge model, the same MCP surface your AI tools already speak, running on your machine with your database and your AI keys. You get vaults, knowledge items with versions and comments, file attachments with text extraction, tags, topics and entities, keyword and vector search on PostgreSQL + pgvector, chat and Q&A over your own content, an inbox for quick capture, Entra ID single sign-on, full import/export, and a Model Context Protocol server exposing the tools listed below. External AI and storage integrations use the services you configure; optional cloud synchronization runs only when you connect a Knowz Cloud account.

## What it is not

It is not a copy of Knowz Cloud. There is no public sign-up — the installer creates your first administrator and every further account is created by an administrator. There is no knowledge graph, no todo tracking, no shared public sites, no billing, no mobile apps, and no managed AI: bring an OpenAI-compatible endpoint, Azure OpenAI, or run offline with search and capture only. The MCP server advertises only the tools this edition can actually run. If you want the full product, that is [Knowz Cloud](https://knowz.io).

## Features

- **Knowledge management** — vaults, knowledge items with versions and comments, tags, topics, and AI-extracted entities
- **Search** — keyword search always; vector similarity search on PostgreSQL + pgvector once an embedding model is configured
- **Chat and Ask** — conversational and single-question answers over your own content, with source citations
- **MCP integration** — a Model Context Protocol server so AI assistants (Claude, Cursor, and friends) can query and write your knowledge
- **Files** — upload and attach files to knowledge items, with text extraction from PDF, DOCX, and plain text
- **Inbox** — quick capture that you triage into vaults later
- **Single sign-on** — Microsoft Entra ID with PKCE and confidential-client modes
- **Data portability** — full import and export in standard formats
- **API-first** — REST API with per-user API keys
- **Enrichment** — automatic extraction, chunking, entity extraction, and summarization when an AI provider is configured
- **Destinations (Connect to Knowz Cloud)** — optional; link this instance to a Knowz Cloud account to browse, pull, and push vault knowledge on demand (no automatic sync)

**Optional integrations.** Azure OpenAI, Azure Blob Storage, and Azure AI Search can be configured if you already run them; none is required. The default stack is PostgreSQL + pgvector with local file storage and an OpenAI-compatible endpoint (or no AI provider at all).

**Hosted OCR egress (off by default).** anydoc has no local OCR. When `Anydoc:Ocr=hosted` is set (env `Anydoc__Ocr=hosted`), a PDF whose pages need OCR is retried once through Firecrawl's hosted Parse API and therefore **leaves the machine**. The optional `Firecrawl:ApiKey` / `Firecrawl:ApiUrl` settings (env `Firecrawl__ApiKey` / `Firecrawl__ApiUrl`) are passed only through the child process environment, never argv. An inherited `FIRECRAWL_API_KEY` / `FIRECRAWL_API_URL` also works; without a key the explicit hosted retry is keyless, and the default URL is `https://api.firecrawl.dev`. Leave hosted OCR disabled to keep document handling fully local.

## Architecture

```
┌───────────────────┐              ┌────────────────────────┐
│   Web UI (React)  │              │   MCP Server (.NET 10) │
│ localhost:3000    │              │   localhost:3001       │
│                   │              │   MCP tools · OAuth    │
└────────┬──────────┘              └──────────┬─────────────┘
         │ /api/* (nginx proxy)               │ HTTP proxy
         │                                    │
┌────────▼────────────────────────────────────▼────────────┐
│  Presentation — API (.NET 10, localhost:5000)            │
│  20 endpoint groups · JWT + API key auth · Rate limiting │
├──────────────────────────────────────────────────────────┤
│  Application — Services                                  │
│  Auth · Knowledge · Vaults · Search · Chat · Enrichment  │
│  Files · Tags · Topics · Import/Export · SSO             │
├──────────────────────────────────────────────────────────┤
│  Infrastructure — Data + External Services               │
│  EF Core (Npgsql) · AI providers · Storage providers     │
│  Content Extraction (PDF, DOCX) · Enrichment Pipeline    │
└──┬────────────────────┬───────────────────┬──────────────┘
   │                    │                   │
┌──▼─────────────────┐ ┌▼────────────────┐ ┌▼─────────────────┐
│ PostgreSQL 16      │ │ AI provider     │ │ File storage     │
│ + pgvector :5432   │ │ (optional)      │ │ (local FS or     │
│ knowz_selfhosted   │ │ OpenAI-compat / │ │  Azure Blob)     │
│ `<=>` vector search│ │ Azure OpenAI    │ │                  │
└────────────────────┘ └─────────────────┘ └──────────────────┘
```

**Services:**

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **API** | .NET 10 Minimal API | REST API, auth, enrichment pipeline, file storage |
| **Web** | React 19 + Vite + Tailwind | Single-page application |
| **MCP** | .NET 10 + MCP SDK | Model Context Protocol server for AI tools |
| **Database** | PostgreSQL 16 + pgvector | Single `knowz_selfhosted` database at internal `db:5432` (no host binding); vector search uses the `<=>` distance operator |
| **File storage** | Local filesystem or Azure Blob | Uploaded files and attachments |
| **AI provider** | OpenAI-compatible, Azure OpenAI, or none | Chat, summarization, entity extraction, embeddings — all optional |

## Compose from a checkout (operators only)

The Apache-2.0 source is available at [knowz-selfhosted-source](https://github.com/knowz-io/knowz-selfhosted-source). Use the CLI above for a guided install, or clone the reviewed source to customize and build it locally. No private repository access is required.

```bash
git clone https://github.com/knowz-io/knowz-selfhosted-source.git
cd knowz-selfhosted-source
cp .env.example .env
# Fill in the REQUIRED secrets in .env (see below) — the stack refuses to start
# without them. Then:
docker compose up -d
```

> **No default credentials ship.** `JWT_SECRET`, `ADMIN_USERNAME`, `ADMIN_PASSWORD`, `MCP_SERVICE_KEY`, and `POSTGRES_PASSWORD` must be set in `.env` — Docker Compose fails fast with a clear error message if any are missing or empty. This is intentional, to prevent accidental deployment with shared/weak credentials.

> **Source-tree wizard:** Run `dotnet run --project src/Knowz.SelfHosted.Setup` for a guided setup that generates `.env` (or `appsettings.Local.json` / user-secrets, depending on your chosen run mode) with strong randomly-generated secrets. See [Setup wizard](#setup-wizard-recommended) below.

The API automatically migrates the database and creates the SuperAdmin account on first startup using the credentials you supplied in `.env`.

### Required Environment Variables

Copy `.env.example` to `.env` and set ALL of the following (no defaults — the stack fails fast if missing):

| Variable | Constraint | Description |
|----------|-----------|-------------|
| `POSTGRES_PASSWORD` | ≥8 chars, mixed character classes recommended | Postgres password for user `knowz` |
| `JWT_SECRET` | ≥32 chars, cryptographically random | JWT signing secret |
| `ADMIN_USERNAME` | non-empty | SuperAdmin username (created on first startup) |
| `ADMIN_PASSWORD` | ≥12 chars; rejected if it appears on the weak-password denylist | SuperAdmin password |
| `MCP_SERVICE_KEY` | non-empty, same value on API + MCP containers | MCP↔API shared secret |

`.env.example` includes copy-pasteable `openssl rand` commands for generating each value.

Optional variables — `MCP_PORT` (default `3001`), AI tier configuration, storage provider, etc. See [Configuration Reference](docs/CONFIGURATION.md).

### Setup Wizard (Recommended)

For first-time setup, the interactive Spectre.Console wizard handles secret generation, AI tier selection, storage choice, and writes the correct config file for your chosen run mode:

```bash
dotnet run --project src/Knowz.SelfHosted.Setup
```

Supports four run modes — Docker Compose (writes `.env`), Aspire Local and Aspire Azure (write user-secrets), and Direct Run (writes `appsettings.Local.json`). Use this instead of hand-editing `.env.example` unless you have automation requirements.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 22+](https://nodejs.org/)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for Postgres + pgvector container)

### Option A: Aspire AppHost (Recommended)

The Aspire AppHost orchestrates all services with a dashboard UI.

```bash
# 1. Install web client dependencies (one-time)
cd src/knowz-selfhosted-web && npm install && cd ../..

# 2. Start everything (Postgres + pgvector container + API + Web + Dashboard)
dotnet run --project src/Knowz.SelfHosted.AppHost --launch-profile local
```

The stack works immediately without AI credentials (auth, CRUD, import/export all functional). To enable AI features, configure via user-secrets:

```bash
cd src/Knowz.SelfHosted.AppHost

# Option 1: Connect to Knowz Cloud (simplest -- just an API key)
dotnet user-secrets set "KnowzPlatform:Enabled" "true"
dotnet user-secrets set "KnowzPlatform:BaseUrl" "https://api.knowz.io"
dotnet user-secrets set "KnowzPlatform:ApiKey" "ukz_your_key"

# Option 2: Your own Azure OpenAI resources
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://your-openai.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey" "your-key"
dotnet user-secrets set "AzureOpenAI:DeploymentName" "gpt-5.4"
dotnet user-secrets set "AzureOpenAI:EmbeddingDeploymentName" "text-embedding-3-large"
# Embedding dim/model — REQUIRED for vector search. Must match the deployed
# model: 3072 for -3-large (default), 1536 for -3-small / ada-002.
dotnet user-secrets set "Embedding:ModelName" "text-embedding-3-large"
dotnet user-secrets set "Embedding:Dimensions" "3072"
dotnet user-secrets set "AzureAISearch:Endpoint" "https://your-search.search.windows.net/"
dotnet user-secrets set "AzureAISearch:ApiKey" "your-key"
dotnet user-secrets set "AzureAISearch:IndexName" "knowledge"
```

See [AppHost README](src/Knowz.SelfHosted.AppHost/README.md) for full details, modes, and tier comparison.

#### Aspire Dashboard

Opens automatically at `https://localhost:17200` showing all services, logs, traces, and metrics.

### Option B: Manual (without Aspire)

```bash
# Terminal 1: Start Postgres + pgvector
docker run -e POSTGRES_USER=knowz -e POSTGRES_PASSWORD=YourPassword123! \
  -e POSTGRES_DB=knowz_selfhosted -p 5432:5432 pgvector/pgvector:pg16

# Terminal 2: API
cd src/Knowz.SelfHosted.API
dotnet run --urls http://localhost:5000

# Terminal 3: Web
cd src/knowz-selfhosted-web
npm install && npm run dev

# Terminal 4: MCP
cd src/Knowz.MCP
dotnet run --urls http://localhost:8080
```

### Option C: Docker Compose

```bash
docker compose up --build
```

## Azure Deployment

**Azure deployment.** Azure deployment is not offered in this snapshot. The templates under `infrastructure/` and `terraform/` provision Microsoft SQL databases and are incompatible with the current PostgreSQL API; they are retained for a future update. Deploy with Docker Compose (above) or the `knowz` CLI.

## Updating

### Knowz CLI (customers)

Use `knowz up` to start or reconnect to an existing installation. Upgrade deliberately to a compatible, published selfhosted version:

```bash
knowz backup --edition selfhosted --encrypt --out knowz-before-upgrade.tar.gz.enc
knowz runtime upgrade --edition selfhosted --version 0.16.2
knowz runtime status --edition selfhosted
```

Use the same `--name` on every command for a named installation. Keep the backup passphrase separately from the archive. Upgrade preserves database, file and protection-key volumes; the API applies any required migrations on startup. If sign-in needs attention, use the current password with `knowz setup --name <name>`; changing the bootstrap password in `.env` does not reset an existing account. See the [Quickstart recovery instructions](docs/QUICKSTART.md#stopping-restarting-and-recovering).

### Compose from a checkout (operators only)

```bash
git pull origin main
docker compose up --build -d
```

Or use the helper script:

```bash
./infrastructure/selfhosted-update-compose.sh
./infrastructure/selfhosted-update-compose.sh --version 0.6.0
```

### Azure and Terraform

**Azure deployment.** Azure deployment is not offered in this snapshot. The templates under `infrastructure/` and `terraform/` provision Microsoft SQL databases and are incompatible with the current PostgreSQL API; they are retained for a future update. Deploy with Docker Compose (above) or the `knowz` CLI.

### What Happens During Update

- Container images are pulled from GHCR (public registry, no auth needed)
- Database migrations run automatically on API startup
- Database, file and protection-key volumes are preserved; keep a verified backup before upgrading
- Services are briefly unavailable while containers restart; duration depends on image downloads and migrations
- Health checks verify all services are running after update

## API Reference

The API exposes the 21 endpoint groups listed below. Swagger is off in the shipped configuration; set `ENABLE_SWAGGER=true` to serve `/swagger` for local debug.

| Group | Prefix | Description |
|-------|--------|-------------|
| Auth | `/api/v1/auth` | Login, token refresh, SSO |
| Account | `/api/v1/account` | User profile, password change |
| API Keys | `/api/v1/apikeys` | Per-user API key management |
| Knowledge | `/api/v1/knowledge` | CRUD for knowledge items |
| Vaults | `/api/v1/vaults` | Vault management |
| Vault Access | `/api/v1/vault-access` | Per-user vault permissions |
| Search | `/api/v1/search` | Semantic + vector search |
| Chat | `/api/v1/chat` | AI chat over knowledge base |
| Topics | `/api/v1/topics` | Topic browsing |
| Tags | `/api/v1/tags` | Tag management |
| Entities | `/api/v1/entities` | AI-extracted entities |
| Files | `/api/v1/files` | File upload and management |
| Comments | `/api/v1/comments` | Knowledge item comments |
| Inbox | `/api/v1/inbox` | Staging area for new items |
| Portability | `/api/v1/portability` | Import/export data |
| Destinations | `/api/v1/sync` | Knowz Cloud connection, vault links, manual pull/push runs, history |
| Config | `/api/v1/config` | Runtime configuration |
| SSO | `/api/v1/sso` | SSO/OIDC configuration |
| Admin | `/api/v1/admin` | Admin operations |
| MCP Internal | `/api/v1/mcp` | Internal endpoints for MCP server |
| Health | `/healthz` | Health check |

**Authentication:** Two schemes supported:
- **JWT Bearer** -- Login via `/api/v1/auth/login`, use `Authorization: Bearer <token>`
- **API Key** -- Generate at `/api/v1/apikeys`, use `X-Api-Key: ksh_...` header

## MCP Server

The MCP server exposes 26 tools for AI assistants to interact with your knowledge base. This edition advertises only the tools it can actually run — the graph, todo, document-windowing, and async-amend tools of Knowz Cloud are not present. Source of truth: `src/Knowz.MCP/Tools/KnowzProxyTools.cs`; `scripts/check-readme-mcp-tools.sh` keeps this list honest.

### Connecting Claude Desktop / Cursor

```json
{
  "mcpServers": {
    "knowz": {
      "url": "http://localhost:3001/mcp",
      "headers": {
        "X-Api-Key": "ksh_your_api_key_here"
      }
    }
  }
}
```

### Available Tools

**Search & retrieval**

| Tool | Description |
|------|-------------|
| `search_knowledge` | Semantic search across the knowledge base |
| `advanced_search` | Search with filters (vault, tags, date range, entities) |
| `list_matching_items` | Filtered list of items matching criteria |
| `search_by_file_pattern` | Search by file path pattern |
| `search_by_title_pattern` | Search by title pattern |
| `get_knowledge_item` | Get a specific knowledge item by ID |
| `bulk_get_knowledge_items` | Retrieve multiple items by ID |
| `list_knowledge_items` | List knowledge items with pagination |
| `count_knowledge` | Count knowledge items with filters |
| `ask_question` | Ask a question over the knowledge base (RAG) |

**Authoring**

| Tool | Description |
|------|-------------|
| `create_knowledge` | Create a new knowledge item |
| `update_knowledge` | Update an existing knowledge item |
| `amend_knowledge` | Apply a natural-language edit instruction; applies synchronously and returns the updated item |
| `create_inbox_item` | Add an item to the inbox staging area |
| `upload_file` | Upload a file to this instance |
| `attach_files` | Attach uploaded files to knowledge items |
| `get_version_history` | Get the version history for a knowledge item |

**Organization**

| Tool | Description |
|------|-------------|
| `list_vaults` | List available vaults |
| `list_vault_contents` | Browse vault contents |
| `create_vault` | Create a new vault |
| `list_topics` | List topics |
| `get_topic_details` | Get topic details |
| `find_entities` | Search AI-extracted entities |
| `get_statistics` | Get knowledge base statistics |

**Collaboration**

| Tool | Description |
|------|-------------|
| `add_comment` | Add a comment to a knowledge item |
| `list_comments` | List comments on a knowledge item |


## Project Structure

```
selfhosted/
├── docker-compose.yml              # Docker Compose for local stack
├── .env.example                    # Environment variable template
├── Knowz.SelfHosted.sln            # Visual Studio solution
├── infrastructure/
│   ├── selfhosted-test.bicep       # Azure Bicep template (infra + Container Apps)
│   ├── selfhosted-test.bicepparam  # Bicep parameter file
│   ├── selfhosted-deploy.ps1       # One-command deployment script
│   └── selfhosted-teardown.ps1     # Resource cleanup script
├── docs/
│   ├── QUICKSTART.md               # 5-minute Docker setup
│   ├── CONFIGURATION.md            # Full environment variable reference
│   └── ARCHITECTURE.md             # System design and data flow
├── src/
│   ├── Knowz.Core/                 # Shared interfaces and types
│   ├── Knowz.SelfHosted.API/       # REST API (Minimal API, .NET 10)
│   │   ├── Endpoints/              # 20 endpoint modules
│   │   ├── Dockerfile              # Multi-stage Docker build
│   │   └── Program.cs              # Startup, DI, auth, middleware
│   ├── Knowz.SelfHosted.Application/
│   │   └── Services/               # Business logic (auth, knowledge, search, etc.)
│   ├── Knowz.SelfHosted.Infrastructure/
│   │   ├── Data/                   # EF Core DbContext, migrations, entities
│   │   └── Services/               # Azure integrations, storage, enrichment
│   ├── Knowz.SelfHosted.AppHost/   # Aspire orchestrator (local/cloud/UI-only modes)
│   ├── Knowz.SelfHosted.Setup/     # Interactive Spectre.Console setup wizard
│   ├── Knowz.SelfHosted.Tests/     # xUnit integration tests
│   ├── Knowz.MCP/                  # MCP server (26 tools, OAuth sessions)
│   ├── Knowz.MCP.Tests/            # MCP unit tests
│   └── knowz-selfhosted-web/       # React SPA (Vite, Tailwind, TypeScript)
│       ├── src/pages/              # 20+ pages
│       ├── src/components/         # Reusable UI components
│       ├── Dockerfile              # nginx + envsubst for runtime config
│       └── vite.config.ts          # Dev server proxy config
└── .github/workflows/
    ├── ci.yml                      # PR checks (build, test, Docker)
    └── release.yml                 # Multi-arch GHCR image publish
```

## AI Features (Optional)

Knowz Self-Hosted is fully usable with no AI service configured: authentication, vaults, knowledge CRUD, files, tags, topics, inbox, keyword search, and import/export all work offline. Configure a provider to add vector search, chat, ask, and automatic enrichment.

| Tier | Provider | Setup | Search |
|------|----------|-------|--------|
| 0 | **None (offline)** | nothing to configure | Keyword search on PostgreSQL |
| 1 | **OpenAI-compatible endpoint** | `OPENAI_COMPAT_ENDPOINT`, model names, `EMBEDDING_DIMENSIONS` | Keyword + pgvector similarity |
| 2 | **Azure OpenAI** | endpoint, key, chat and embedding deployments, `EMBEDDING_DIMENSIONS` | Keyword + pgvector similarity |
| 3 | **Connect to Knowz Cloud** | a Knowz Cloud API key | Keyword search locally; AI operations answered by Knowz Cloud |

Tier 1 covers anything speaking `/v1/chat/completions` and `/v1/embeddings` — Ollama, LM Studio, vLLM, or a hosted OpenAI-compatible API. Set `EMBEDDING_DIMENSIONS` to match your embedding model (for example `768` for `nomic-embed-text`, `3072` for `text-embedding-3-large`); embeddings are stored in pgvector.

Tier 3 is opt-in and off by default. Nothing leaves this instance until you supply a Knowz Cloud key.

See [Configuration Reference](docs/CONFIGURATION.md) for setup instructions.


### Enrichment Pipeline

When an AI provider is configured, the enrichment pipeline automatically processes new knowledge items:

1. **Content Extraction** -- PDF, DOCX, XLSX, PPTX, legacy DOC/XLS/PPT, ODT/ODS/ODP, RTF, EPUB and text files parsed to plain text or markdown locally (via the bundled `anydoc` CLI); no cloud call required
2. **Chunking** -- Content split into overlapping chunks for embedding
3. **Entity Extraction** -- AI identifies people, places, organizations, concepts
4. **Summarization** -- AI generates concise summaries
5. **Vector Embedding** -- Content embedded for semantic search
6. **Search indexing** -- Chunks and embeddings written to PostgreSQL for retrieval

Without an AI provider configured, knowledge items are stored and accessible via keyword search, tags, topics, and manual organization.

## CI/CD

### Pull Request Checks (`ci.yml`)

Runs on PRs to `main`:
- .NET build and test (all projects)
- Node.js build and test (web client)
- Docker image builds (no push)

### Release (`release.yml`)

Official image publication runs from the controlled build repository on version tags (`v*.*.*`). The public source repository and forks do not publish official image tags.
- Multi-architecture builds (`linux/amd64`, `linux/arm64`)
- Stages GHCR images under version tags; existing versions and `latest` are preserved:
  - `ghcr.io/knowz-io/knowz-selfhosted-api`
  - `ghcr.io/knowz-io/knowz-selfhosted-web`
  - `ghcr.io/knowz-io/knowz-selfhosted-mcp`

A release descriptor is staged only after the complete multi-architecture set and its startup check pass. Stable promotion additionally requires the installed CLI acceptance journey.

## Testing

```bash
# .NET tests
dotnet test Knowz.SelfHosted.sln

# Web client tests
cd src/knowz-selfhosted-web
npm test

# Full Docker stack
docker compose up --build
# Then: http://localhost:3000
```

## Documentation

| Document | Description |
|----------|-------------|
| [Quickstart Guide](docs/QUICKSTART.md) | Customer install via `npx @knowzai/cli@0.5.0 up` / `knowz up` (release images, no source) |
| [Configuration Reference](docs/CONFIGURATION.md) | All environment variables and settings |
| [Architecture Overview](docs/ARCHITECTURE.md) | System design, service diagram, data flow |
| [Contributing](CONTRIBUTING.md) | Development setup, PR process, coding standards |
| [Security Policy](SECURITY.md) | Vulnerability reporting and security practices |

## License

This project is licensed under the [Apache License 2.0](LICENSE).

Copyright 2026 Knowz AI (knowzai.com)
