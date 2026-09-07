# Knowz Self-Hosted — Upgrade Notes

Release-specific operational guidance for self-hosted customers upgrading between versions. Read the section for the version you are moving **to** before pulling the image.

---

## Hosted MCP AllowedHosts vs selfhosted harden — GitHub #905 / #875

`#875` incorrectly tightened **MCP `appsettings.json`** `AllowedHosts` to `localhost;127.0.0.1;[::1]`. `#867` intended compose-only harden; that file is copied into GHCR `knowz-mcp` (`src/Knowz.MCP/Dockerfile`). On v0.44.2, Container Apps stayed Healthy (probes use `Host: localhost`) while `https://mcp.knowz.io/healthz` returned AGW 502 (public `Host: mcp.knowz.io` rejected by HostFiltering).

**Split (do not collapse these):**

| Surface | Default |
|---------|---------|
| MCP `appsettings.json` (shared / hosted image) | `*` — required for FD/AGW and Azure/custom hostnames |
| Selfhosted compose `ALLOWED_HOSTS` | localhost + loopback + `mcp`. Set `ALLOWED_HOSTS` for a public hostname. |
| Hosted Dockerfile ENV + `mcp.bicep` / App Service | `AllowedHosts=*` (env-overridable belt-and-suspenders) |

Do not bake localhost-only back into MCP `appsettings.json`.

---

## Kit hardening (DB host port, Swagger, AllowedHosts) — GitHub #867

Live published kit is **0.16.0 Postgres**. Overnight QA confirmed `EnableSwagger` is already `false` in that cut. The original 0.15.1 findings (SQL `"1433:1433"` on all interfaces, Swagger on in `appsettings.json`) are **obsolete** — do not patch 0.15.1 in place.

This change hardens the **0.16.0 / develop `selfhosted/`** defaults that the next recopy ships:

| Default | 0.16.0 published kit (live today) | After this change (`selfhosted/`) |
|---------|-----------------------------------|-----------------------------------|
| Database host port | Loopback `127.0.0.1:${DB_PORT:-5432}:5432` | Not published. Postgres stays on the compose network (`db:5432`) |
| Swagger / OpenAPI | Off in shipped `appsettings.json` | Still off. Also config-gated only — `IsDevelopment()` no longer forces Swagger on. Local debug: `ENABLE_SWAGGER=true` or `appsettings.Development.json` |
| `AllowedHosts` | `*` in API and MCP `appsettings.json` | `localhost;127.0.0.1;[::1]`. Compose also adds the container DNS name (`api` / `mcp`) |

### What you need to do

**On 0.16.0 today (before the next recopy):**

- Database tools on the host already work via the loopback publish. After you recopy, uncomment the loopback `ports:` block under `db` if you still need that.
- Swagger is already off. Leave it off.
- If you expose a public hostname, set `ALLOWED_HOSTS` (the 0.16.0 image still has `*`; the next image cut does not).

**After the next recopied release:**

- Host-side `psql`: uncomment the loopback `ports:` block under `db` in `docker-compose.yml`. Do not publish on `0.0.0.0`.
- Local Swagger: `ENABLE_SWAGGER=true` in `.env`, then `docker compose up -d api`.
- Custom / Azure hostname: set `ALLOWED_HOSTS` (or `AllowedHosts`) to that FQDN plus `localhost;api;mcp` as needed. Bare `*` is no longer the image default.

CLI pull-kit `deploy/selfhosted/docker-compose.yml` (0.16.0 `knowz up`) is a separate asset and still publishes loopback `5432` (VERIFY-K17). That is not this recopy.

---

## Package Version Policy (2026-05-16+)

| Package family | Strategy |
|---|---|
| `System.IdentityModel.Tokens.Jwt` / `Microsoft.IdentityModel.*` | Align JWT to the latest 8.x patch (currently **8.16.0**) across all self-hosted projects. `Microsoft.IdentityModel.Protocols.OpenIdConnect` tracks the matched family release (currently **8.4.0**) — bump only when Microsoft ships matched versions. |
| `Azure.Storage.Blobs` | Pin to a single version across API + Infrastructure. Bump as a single commit (currently **12.27.0**). |
| `ModelContextProtocol.AspNetCore` | Preview track. Pinned to an exact preview build (currently **0.4.1-preview.1**, no floating range). Roll to GA when available. |

Last alignment: 2026-05-16, WorkGroup `kc-feat-selfhosted-platform-sync-20260516-162852` (`SelfHostedHealthHygiene` NodeID).

---

## Upgrading to the release containing `FIX_SelfhostedVectorDimsConfigurable`

**What changed:** The self-hosted API previously hardcoded the vector-search index dimension to `1536`. It now reads the dimension from configuration (`Embedding:Dimensions`), matching Knowz Cloud's `ARCH_EmbeddingConfigOwnership` design. The change is mandatory for parity and for supporting higher-dim embedding models (e.g. `text-embedding-3-large` → 3072).

### What you need to do

1. **Add the `Embedding` config block** before pulling the new image. Two new keys are required whenever Azure AI Search is in use:

   | Config key | Example | Notes |
   |---|---|---|
   | `Embedding:ModelName` (`Embedding__ModelName` env) | `text-embedding-3-small` | Must match the Azure OpenAI embedding deployment's model |
   | `Embedding:Dimensions` (`Embedding__Dimensions` env) | `1536` | **Must match the model's output dim:** `text-embedding-3-small` / `text-embedding-ada-002` → `1536`, `text-embedding-3-large` → `3072` |

   See `selfhosted/docs/CONFIGURATION.md` for the full table.

2. **Supply it via the mechanism you deployed with:**

   - **ARM / Azure Portal deployment (Standard or Enterprise):** re-run the deployment with the new `embeddingDimensions` (and `embeddingModelNameParam` on Enterprise) parameter. The templates default to `1536`; set explicitly if you're on `-3-large`.
   - **Bicep:** re-deploy `selfhosted-test.bicep` or `selfhosted-enterprise.bicep` with the new `embeddingDimensions` param. Container Apps pick up `Embedding__ModelName` / `Embedding__Dimensions` env vars.
   - **Terraform (Standard or Enterprise):** set `embedding_dimensions` in `terraform.tfvars`, `terraform apply`.
   - **Docker Compose:** set `EMBEDDING_MODEL_NAME` / `EMBEDDING_DIMENSIONS` in `.env` (both default to `text-embedding-3-small` / `1536` if unset).
   - **Manual / user-secrets:** `dotnet user-secrets set "Embedding:ModelName" "..."` and `dotnet user-secrets set "Embedding:Dimensions" "..."`.
   - **Key Vault:** add two secrets `Embedding--ModelName` and `Embedding--Dimensions`. `selfhosted/scripts/setup-sh-dev.ps1` pulls both into user-secrets automatically.

3. **Restart the API container.** If `Embedding:Dimensions` is not configured, the API fails to start with a clear error message and a pointer to `ARCH_EmbeddingConfigOwnership`. Fix the config and restart.

### Dim mismatch with an existing index

If your configured `Embedding:Dimensions` does not match the dim of the `contentVector` field in the already-deployed Azure AI Search index, vector search will return no results and/or fail at ingest time with a dim mismatch. The index field dim is fixed at index creation time — you cannot edit it in place. Options:

| Situation | Action |
|---|---|
| You are still on the same embedding model, just setting the config explicitly | Set `Embedding:Dimensions = 1536` (the prior hardcoded value) — nothing else changes. |
| You are switching embedding models (e.g. `-3-small` → `-3-large`) | Delete the search index, deploy the new config, then re-ingest. See below. |

**Selfhosted does not yet have a SuperAdmin "wipe + reprocess" endpoint** (that ships with Knowz Cloud via `FEAT_WipeAndReprocessEmbeddings`; the self-hosted parity NodeID `FEAT_SelfhostedWipeAndReprocess` is parked — no install base yet). For now, the manual procedure is:

```bash
# 1. Delete the search index (REST)
curl -X DELETE "https://<your-search>.search.windows.net/indexes/knowledge?api-version=2024-07-01" \
    -H "api-key: <admin-key>"

# 2. Update Embedding:Dimensions + AzureOpenAI:EmbeddingDeploymentName in config

# 3. Restart the API container. The index is auto-created on first use
#    (selfhosted/src/Knowz.SelfHosted.Infrastructure/Services/AzureSearchService.cs
#    — EnsureIndexExistsAsync) with the new dim.

# 4. Re-upload / re-sync your knowledge so new embeddings are generated.
```

### Verification

After restart, confirm the new dim is in use:

```bash
curl -s "https://<your-search>.search.windows.net/indexes/knowledge?api-version=2024-07-01" \
    -H "api-key: <admin-key>" \
    | jq '.fields[] | select(.name=="contentVector") | {name, dimensions: .dimensions}'
```

Expected output: `{"name":"contentVector","dimensions":1536}` (or `3072` if you switched).

The post-deploy smoke script (`selfhosted/scripts/post-deploy-smoke.sh`) exercises this end-to-end via its semantic-search step — a dim mismatch manifests as "seed content not found", failing the smoke loudly.
