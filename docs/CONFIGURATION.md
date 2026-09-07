# Configuration Reference

All configuration is done through environment variables in your `.env` file (Docker Compose), user-secrets (Aspire), or `appsettings.Local.json`. This document covers every configurable option organized by category.

## Required

These variables MUST be set for the application to start. **No defaults ship** — Docker Compose uses `${VAR:?...}` substitution that fails fast with a clear error message if any are missing or empty. This is intentional: it prevents accidental deployment with shared or weak credentials. The Setup wizard (`dotnet run --project src/Knowz.SelfHosted.Setup`) generates all of these automatically.

| Variable | Constraint | Description |
|----------|-----------|-------------|
| `POSTGRES_PASSWORD` | ≥8 chars, mixed character classes recommended | Postgres password for user `knowz`. |
| `JWT_SECRET` | ≥32 chars, cryptographically random | Secret key used to sign JWT authentication tokens. |
| `ADMIN_USERNAME` | non-empty | SuperAdmin username created on first startup. |
| `ADMIN_PASSWORD` | ≥12 chars; rejected at startup if on `AuthService.WeakPasswordList` (`changeme`, `password`, `admin`, etc.) | SuperAdmin password created on first startup. |
| `MCP_SERVICE_KEY` | non-empty; identical value on API + MCP containers | Shared secret for MCP↔API internal service-to-service calls (email/password login, SSO resolve). |

Generation helpers (also in `.env.example`):

```bash
# Linux/macOS
openssl rand -base64 48 | tr -d '/+=\n' | head -c 64   # JWT_SECRET
openssl rand -base64 24                                # ADMIN_PASSWORD / POSTGRES_PASSWORD / MCP_SERVICE_KEY

# Windows PowerShell
[Convert]::ToBase64String((1..48 | %{Get-Random -Max 256}) | %{[byte]$_})
```

## AI Services (Optional)

AI is optional. Configure **one** provider, or none at all:

| Tier | Trigger | AI Operations | Search | Best for |
|------|---------|---------------|--------|----------|
| 0 | Nothing configured (default) | Disabled (NoOp) | Keyword only | Offline capture, auth/admin/CRUD |
| 1 | `OpenAiCompatible:Endpoint` + models | Your OpenAI-compatible endpoint | Keyword + pgvector similarity | Local models (Ollama, LM Studio, vLLM) or any `/v1` API |
| 2 | `AzureOpenAI` configured | Direct Azure OpenAI calls | Keyword + pgvector similarity | You already run Azure OpenAI |
| 3 | `KnowzPlatform:Enabled=true` + BaseUrl + ApiKey | Answered by Knowz Cloud | Keyword only | Quick setup, no AI infrastructure |

Embeddings are stored in PostgreSQL `vector` columns (pgvector) and searched with the `<=>` cosine-distance operator, whichever provider generated them. `EMBEDDING_DIMENSIONS` must match the model you configure.

### Tier 1: OpenAI-compatible endpoint

Any service exposing `/v1/chat/completions` and `/v1/embeddings` — Ollama, LM Studio, vLLM, llama.cpp, or a hosted OpenAI-compatible API. Nothing leaves your network if the endpoint is local.

| Variable | Config Key | Description | Example |
|----------|-----------|-------------|---------|
| `OPENAI_COMPAT_ENDPOINT` | `OpenAiCompatible__Endpoint` | Base URL of the OpenAI-compatible API | `http://host.docker.internal:1234/v1` |
| `OPENAI_COMPAT_APIKEY` | `OpenAiCompatible__ApiKey` | API key, if the endpoint requires one (may be empty for local runtimes) | `sk-...` |
| `OPENAI_COMPAT_CHAT_MODEL` | `OpenAiCompatible__ChatModel` | Chat/completion model identifier | `qwen2.5-7b-instruct` |
| `OPENAI_COMPAT_EMBEDDING_MODEL` | `OpenAiCompatible__EmbeddingModel` | Embedding model identifier | `nomic-embed-text` |
| `EMBEDDING_DIMENSIONS` | `Embedding__Dimensions` | Embedding vector dimension count. **Required** — must match the embedding model above (for example `768` for `nomic-embed-text`, `1024` for `mxbai-embed-large`). | `768` |

> Changing the embedding model after content has been embedded requires re-embedding: a dimension mismatch between stored vectors and new queries breaks vector search. See [UPGRADE_NOTES.md](UPGRADE_NOTES.md).

### Tier 2: Azure OpenAI (Optional)

Enable AI-powered chat, summarization, and embedding generation with your own Azure OpenAI resource.

| Variable | Config Key | Description | Example |
|----------|-----------|-------------|---------|
| `AZURE_OPENAI_ENDPOINT` | `AzureOpenAI__Endpoint` | Azure OpenAI resource endpoint URL | `https://your-openai.openai.azure.com/` |
| `AZURE_OPENAI_API_KEY` | `AzureOpenAI__ApiKey` | Azure OpenAI API key | `your-api-key` |
| `AZURE_OPENAI_DEPLOYMENT` | `AzureOpenAI__DeploymentName` | Chat/completion model deployment name | `gpt-5.4` |
| `AZURE_OPENAI_EMBEDDING` | `AzureOpenAI__EmbeddingDeploymentName` | Embedding model deployment name | `text-embedding-3-large` |
| `EMBEDDING_MODEL_NAME` | `Embedding__ModelName` | Embedding model identifier (must match deployed model) | `text-embedding-3-large` |
| `EMBEDDING_DIMENSIONS` | `Embedding__Dimensions` | Embedding vector dimension count. **Required** for vector search. Must match the deployed model: `3072` for `text-embedding-3-large` (default), `1536` for `text-embedding-3-small` / `ada-002`. API fails to start if this is missing. See `ARCH_EmbeddingConfigOwnership`. | `3072` |

### Tier 2 add-on: Azure AI Search (Optional)

Enable semantic vector search across your knowledge base. Requires Azure OpenAI to also be configured (for generating embeddings).

| Variable | Config Key | Description | Example |
|----------|-----------|-------------|---------|
| `AZURE_AI_SEARCH_ENDPOINT` | `AzureAISearch__Endpoint` | Azure AI Search service endpoint | `https://your-search.search.windows.net` |
| `AZURE_AI_SEARCH_API_KEY` | `AzureAISearch__ApiKey` | Azure AI Search admin API key | `your-api-key` |
| -- | `AzureAISearch__IndexName` | Search index name | `knowledge` |

> **Embedding dim/model parity (important):** The search index `contentVector` field is created with the dimension count in `Embedding__Dimensions`. Changing the embedding model after the index is created (e.g. `-3-small` → `-3-large`) requires dropping the index and re-ingesting — a dim mismatch between the index schema and new embeddings silently breaks vector search. See `selfhosted/docs/UPGRADE_NOTES.md` for the runbook.

### Tier 3: Connect to Knowz Cloud

Delegates AI operations (completions, embeddings, summarization, entity extraction, enrichment) to Knowz Cloud. No Azure subscription required -- just an API key from your Knowz Cloud account.

| Variable | Config Key | Description | Example |
|----------|-----------|-------------|---------|
| -- | `KnowzPlatform__Enabled` | Enable the Knowz Cloud connection | `true` |
| -- | `KnowzPlatform__BaseUrl` | Knowz Cloud API base URL | `https://api.knowz.io` |
| -- | `KnowzPlatform__ApiKey` | Knowz Cloud API key | `ukz_...` |

**What works:** All AI operations (chat, summarization, embeddings, entity extraction, enrichment) are answered by Knowz Cloud. Search stays local and uses keyword matching.

**Aspire user-secrets:**
```bash
dotnet user-secrets set "KnowzPlatform:Enabled" "true" --project src/Knowz.SelfHosted.AppHost
dotnet user-secrets set "KnowzPlatform:BaseUrl" "https://api.knowz.io" --project src/Knowz.SelfHosted.AppHost
dotnet user-secrets set "KnowzPlatform:ApiKey" "ukz_your_key" --project src/Knowz.SelfHosted.AppHost
```

## Storage

Configure where uploaded files are stored. The default local filesystem provider requires no external services.

| Variable | Default | Description |
|----------|---------|-------------|
| `STORAGE_PROVIDER` | `LocalFileSystem` | Storage backend: `LocalFileSystem` or `AzureBlobStorage` |
| `AZURE_STORAGE_CONNECTION_STRING` | -- | Azure Blob Storage connection string (when using Azure provider) |
| `AZURE_STORAGE_CONTAINER` | `selfhosted-files` | Blob container name (when using Azure provider) |

These map to the following compose-level configuration keys:

| Compose Environment Key | Default | Description |
|--------------------------|---------|-------------|
| `Storage__Provider` | `LocalFileSystem` | Storage backend |
| `Storage__Local__RootPath` | `/data/files` | Local filesystem path for file storage (inside the container) |
| `Storage__Azure__ConnectionString` | -- | Azure Blob Storage connection string |
| `Storage__Azure__ContainerName` | `selfhosted-files` | Blob container name |

The default `LocalFileSystem` provider stores files in a Docker volume (`knowz-file-storage`), which persists across container restarts.

## File Cleanup on Detach

Controls what happens to the underlying file when it is detached from a knowledge item.

| Compose Environment Key | Default | Description |
|--------------------------|---------|-------------|
| `FileCleanup__Mode` | `PromptUser` | `AutoCleanup`, `PreserveAlways`, or `PromptUser` |

- **`PromptUser`** (default, safest): the web client asks whether to also delete the file. Detach requests with no explicit choice **preserve** the file (only the link is removed). API/MCP consumers that send no `deleteFiles` param always preserve.
- **`AutoCleanup`**: detaching always deletes the underlying file (when no other item references it), with no prompt.
- **`PreserveAlways`**: detaching never deletes the underlying file — it only removes the link.

A file referenced by another knowledge item or comment is **always preserved** regardless of mode (last-link safety guard).

> **Data-loss warning:** Unlike Knowz Cloud, this edition has **no grace period** — when a file is deleted it is removed from storage **immediately and irreversibly**. Setting `FileCleanup__Mode=AutoCleanup` makes every plain detach destructive. Previously, detaching a file silently orphaned the file record and blob forever; it now correctly preserves (default) or deletes (opt-in).

## SSO (Single Sign-On)

SSO with Microsoft Entra ID (Azure AD) is configured through the Admin UI after login, not through environment variables. This section is for reference.

The application supports two Entra ID modes:

- **PKCE (public client)** -- Browser-based flow, no client secret required
- **Confidential client** -- Server-side flow with client secret

Settings configured in the Admin UI:

| Setting | Description |
|---------|-------------|
| Client ID | Application (client) ID from Azure portal |
| Client Secret | Client secret value (confidential mode only) |
| Directory (Tenant) ID | Azure AD tenant ID |
| Auto-provision users | Automatically create user accounts on first SSO login |

## MCP (Model Context Protocol)

The MCP server acts as a proxy, allowing AI tools (Claude, Cursor, etc.) to query your knowledge base through the standardized MCP protocol.

| Variable | Default | Description |
|----------|---------|-------------|
| `MCP_PORT` | `3001` | Host port for the MCP server |
| `MCP_API_URL` | `http://api:8080` | Internal API URL that MCP proxies to. Change only if using a custom network setup. |
| `MCP_SERVICE_KEY` | *(none — REQUIRED)* | Shared secret between MCP server and API for internal service-to-service calls (email/password login, SSO resolve). Compose fails fast if unset. Same value on both containers; generate with `openssl rand -base64 24`. |
| `MCP_VALIDATE_API_KEY` | `true` | Whether MCP validates API keys on incoming requests |

These map to the following compose-level configuration keys:

| Compose Environment Key | Default | Description |
|--------------------------|---------|-------------|
| `Knowz__BaseUrl` | `http://api:8080` | Internal API URL for proxying |
| `Authentication__ValidateApiKey` | `true` | API key validation toggle |
| `MCP__BackendMode` | `selfhosted` | Backend mode (set automatically in compose) |
| `MCP__ServiceKey` | *(none — from `MCP_SERVICE_KEY`, REQUIRED)* | Shared secret for MCP→API internal calls |
| `MCP__ApiKeyValidationEndpoint` | `/api/vaults` | Endpoint used to validate API keys (set automatically in compose) |

## Advanced

Simplified environment variables for `.env`:

| Variable | Default | Description |
|----------|---------|-------------|
| `RATE_LIMITING_ENABLED` | `true` | Enable API rate limiting |
| `ALLOWED_ORIGIN` | `http://localhost:3000` | CORS allowed origin. Set to your domain in production. |
| `ENABLE_SWAGGER` | `false` | Enable Swagger UI at `/swagger`. Off in compose. Set `true` only for local debug. |
| `ALLOWED_HOSTS` | `localhost;127.0.0.1;[::1];api` (API) / `…;mcp` (MCP) | ASP.NET host filter. Compose does **not** use bare `*`. Set this to your public hostname when the stack is reached by a custom `Host` header. |
| `DB_PORT` | *(unset — no host publish)* | Only used if you uncomment the `db` `ports:` block in `docker-compose.yml`. Loopback bind recommended: `127.0.0.1:${DB_PORT:-5432}:5432`. |

All compose-level configuration keys (for fine-grained control, edit `docker-compose.yml` directly):

| Compose Environment Key | Default | Description |
|--------------------------|---------|-------------|
| `Database__AutoMigrate` | `true` | Automatically run EF Core migrations on API startup |
| `SelfHosted__EnableSwagger` | `${ENABLE_SWAGGER:-false}` | Enable Swagger UI at `/swagger` (local debug) |
| `AllowedHosts` | `${ALLOWED_HOSTS:-localhost;127.0.0.1;[::1];api}` | Host filter. MCP `appsettings.json` is `*` (copied into GHCR `knowz-mcp`; localhost-only there breaks public Host headers — #905). Compose keeps the local harden via `ALLOWED_HOSTS`. API `appsettings.json` stays localhost-only. Custom-hostname selfhosted deploys **must** set `ALLOWED_HOSTS` (or `AllowedHosts`) to the public FQDN. |
| `SelfHosted__JwtExpirationMinutes` | `1440` | JWT token expiration in minutes (default: 24 hours) |
| `SelfHosted__AllowedOrigins__0` | `http://localhost:3000` | CORS allowed origin. Set to your domain in production. |
| `SelfHosted__RateLimiting__Enabled` | `true` | Enable API rate limiting |
| `SelfHosted__RateLimiting__Global__PermitLimit` | `100` | Maximum requests per window (global) |
| `SelfHosted__RateLimiting__Global__WindowSeconds` | `60` | Rate limit window duration in seconds (global) |
| `SelfHosted__RateLimiting__Auth__PermitLimit` | `5` | Maximum authentication attempts per window |
| `SelfHosted__RateLimiting__Auth__WindowSeconds` | `15` | Rate limit window for authentication in seconds |
| `AzureKeyVault__Enabled` | `false` | Enable Azure Key Vault for secret management |
| `AzureKeyVault__VaultUri` | -- | Azure Key Vault URI (e.g., `https://your-vault.vault.azure.net/`) |

## Connection Strings

The database connection string is configured internally in the compose file. If you need to customize it:

| Compose Environment Key | Description |
|--------------------------|-------------|
| `ConnectionStrings__McpDb` | Npgsql connection string. Default uses the `db` service with `POSTGRES_PASSWORD`. |

The default connection string in compose is:

```
Host=db;Port=5432;Database=knowz_selfhosted;Username=knowz;Password=${POSTGRES_PASSWORD}
```

You generally do not need to change this unless you are using an external Postgres instance.

Postgres is reachable on the compose network as `db:5432`. The kit does **not** publish a host port. To use `psql` or another host-side tool against the bundled database, uncomment the loopback mapping in `docker-compose.yml`:

```yaml
# under services.db
ports:
  - "127.0.0.1:${DB_PORT:-5432}:5432"
```

Do not publish `5432:5432` on all interfaces. The 0.16.0 published compose used a loopback bind; that mapping is now opt-in only.

## Production Checklist

Before deploying to production:

- [ ] Set `POSTGRES_PASSWORD` to a strong, unique password (compose fails fast if unset)
- [ ] Set `JWT_SECRET` to a random 64+ character string (compose fails fast if unset)
- [ ] Set a strong `ADMIN_PASSWORD` (or change it immediately after first login)
- [ ] Set `MCP_SERVICE_KEY` to a random string — shared secret between MCP and API (compose fails fast if unset)
- [ ] Set `SelfHosted__AllowedOrigins__0` to your actual domain
- [ ] Set `ALLOWED_HOSTS` to that same hostname (plus `localhost;api` if you still use the compose network)
- [ ] Place the stack behind a reverse proxy with TLS (HTTPS)
- [ ] Leave `ENABLE_SWAGGER` unset/`false` (Swagger is already off)
- [ ] Do not publish the database host port; keep `db` on the internal network
- [ ] Review rate limiting settings for your expected traffic
