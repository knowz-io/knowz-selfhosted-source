<!-- markdownlint-disable MD013 MD060 -->
<!-- MD013 line-length and MD060 table-pipe-compact-style are intentionally
     disabled: tables in this runbook carry long reference URLs, CLI commands,
     and cross-spec references that don't benefit from hard-wrapping. The
     skill's emitted runbook is consumed by ops teams; readability in
     rendered Markdown is what matters, not source-line width. -->

> **Not offered in this snapshot.** This guide describes the Microsoft SQL-era Azure deployment. The current self-hosted API runs on PostgreSQL + pgvector and these templates are incompatible with it. Retained for reference and for a future update. Use Docker Compose or the `knowz` CLI.

# Knowz Self-Hosted Enterprise Deployment Runbook

> **Audience**: operators deploying Knowz self-hosted into an enterprise
> customer's Azure tenant. Assumes familiarity with Azure landing zones,
> Container Apps, private endpoints, and Managed Identities.
>
> **Automated path**: `/deploy-selfhosted --enterprise` (Claude Code skill).
> Every phase below also provides the manual `az`/`pwsh` equivalent so ops
> teams without Claude can self-serve.
>
> **Primary spec**: `knowzcode/specs/SH_ENTERPRISE_SKILL.md` §2.2 (runbook)
> and §2.3 (alerts).
> **WorkGroup**: `kc-feat-sh-enterprise-deploy-20260418-144500`.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Phase 0 — Pre-flight](#phase-0--pre-flight)
3. [Phase 1 — Configuration (with enterprise prompts)](#phase-1--configuration-with-enterprise-prompts)
4. [Phase 1.3.5 — Sentry DSN](#phase-135--sentry-dsn)
5. [Phase 1.5 — BYO capture](#phase-15--byo-capture)
6. [Phase 2 — AI service resolution](#phase-2--ai-service-resolution)
7. [Phase 3 — Deploy (Bicep what-if → apply)](#phase-3--deploy-bicep-what-if--apply)
8. [Phase 4 — Post-deploy (GHCR PAT, revisions)](#phase-4--post-deploy-ghcr-pat-revisions)
9. [Phase 5 — Smoke test](#phase-5--smoke-test)
10. [Phase 6 — Front Door private-link approval](#phase-6--front-door-private-link-approval)
11. [Troubleshooting Matrix](#troubleshooting-matrix)
12. [Day-1 Rotation Playbook](#day-1-rotation-playbook)
13. [Backup / Restore](#backup--restore)
14. [MI Rotation Policy (180-day cadence)](#mi-rotation-policy-180-day-cadence)
15. [Monitoring & alerts](#monitoring--alerts)
16. [Cross-reference: enterprise spec set](#cross-reference-enterprise-spec-set)

---

## Prerequisites

### Azure subscription

- **Role**: Owner, or Contributor + User Access Administrator. UAA is
  required so the deployer can grant the MI role assignments in Phase 3.
- **Quota** in the target region:
  - Azure OpenAI: 20 cores (unless BYO per Phase 1.5).
  - Container Apps: 4 vCPU + 8 GiB memory in the environment.
  - SQL Database: S1 tier (default per `SH_ENTERPRISE_BICEP_HARDENING.md`).

### Network

- **VNet CIDR plan**, agreed with the customer network team:
  - Container Apps environment subnet: `/23` minimum, no delegations
    conflicting with Container Apps Environment requirements.
  - Private endpoint subnet: `/28` minimum, **non-delegated** (the
    `alz-assert-pe-subnet` Bicep guard fails at template-evaluation time
    if both `byoVnetPeSubnetId` and `peSubnetAddressPrefix` are unset —
    hardening added after the 2026-04-17 partner outage).

### Identity

- **Entra ID admin group OID** — captured as `aadAdminObjectId` for SQL AAD
  auth and `aadAdminEmail` for alerting.
- **Fine-grained GitHub PAT** with `read:packages` scope, 90-day expiry.
  Required unless the customer supplies an external ACR (Phase 1.5).
- **AAD app registration** — only required when BYO OpenAI is in a
  different tenant. The app needs `Cognitive Services User` on the
  customer's OpenAI resource.

### Optional

- **Sentry DSN** — format `https://<key>@<org>.ingest.sentry.io/<project>`.
- **Customer-owned resources** (any subset):
  - Azure OpenAI resource with `gpt-5.4`, `gpt-5.4-mini`, and `text-embedding-3-large`
    deployments.
  - Key Vault (must be RBAC-mode, not access-policy).
  - Log Analytics Workspace.
  - Azure Container Registry (replacing GHCR).

### Governance

- Diagnostic-settings target agreed with the customer's observability team
  (central Log Analytics vs per-RG).
- Change-management window booked: deploy + smoke together take ~45 min;
  add 30 min buffer for FD PL approval and first rotation checks.

---

## Phase 0 — Pre-flight

**Skill does automatically**: detects repo context, checks Azure CLI
install, verifies login, prompts for subscription, registers required
providers, scans for soft-deleted resources.

**Manual equivalent**:

```bash
az login --tenant "<customer-tenant-id>"
az account set --subscription "<subscription-id>"

# Register required providers (idempotent):
for provider in \
    Microsoft.CognitiveServices Microsoft.Search Microsoft.Sql \
    Microsoft.Storage Microsoft.KeyVault Microsoft.Insights \
    Microsoft.OperationalInsights Microsoft.App Microsoft.ManagedIdentity \
    Microsoft.Network Microsoft.Cdn; do
    az provider register --namespace "$provider" --wait
done

# Scan for soft-deleted resources that would collide:
az cognitiveservices account list-deleted -o table
az keyvault list-deleted -o table
```

---

## Phase 1 — Configuration (with enterprise prompts)

**Skill does automatically**: gathers resource group, location, prefix,
SKUs, AI service selection per-service (Deploy New / Use Existing / Skip),
and optional features. The `--enterprise` flag adds the BYO and Sentry
subsections below.

**Standard-flow credential prompts are skipped in enterprise** — MI-only
auth per `SH_ENTERPRISE_MI_SWAP.md`; SuperAdmin password, JWT secret, and
bootstrap API key are minted by `APP_CredentialBootstrap` on first
container start (spec §1.2 in `SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP.md`).

**Manual equivalent**: populate a `.bicepparam` / `main.parameters.json`
file with the captured values.

---

## Phase 1.3.5 — Sentry DSN

**Enterprise only.** Optional.

- **Prompt**: "Configure error tracking (Sentry)?"
- **Validation regex**: `^https://[a-f0-9]+@[a-z0-9.]+\.ingest\.sentry\.io/[0-9]+$`.
- **Persistence**: Phase 3 writes the DSN to Key Vault secret `sentrydsn`.
  If the user answers "No", an empty string is written so Bicep's
  `secretRef` resolves.

**Manual equivalent** — after deploy:

```bash
az keyvault secret set \
    --vault-name "$KV_NAME" \
    --name sentrydsn \
    --value "https://<key>@<org>.ingest.sentry.io/<project>"
```

---

## Phase 1.5 — BYO capture

**Enterprise only.** Every prompt format-validates or calls an `az` probe
before accepting. Invalid inputs re-prompt up to 2 more times, then abort
the skill (per `SH_ENTERPRISE_BYO_INFRA.md`).

| Prompt | Captured parameter | Validation |
|---|---|---|
| BYO VNet subnet for Container Apps? | `byoVnetSubnetId` | `az network vnet subnet show --ids` returns 200 |
| BYO PE subnet? | `byoVnetPeSubnetId` OR `peSubnetAddressPrefix` | subnet show + `delegations == []`; CIDR is RFC-1918 |
| BYO Key Vault? | `byoKeyVaultId` | `az keyvault show --ids` returns 200 + RBAC mode |
| BYO Log Analytics Workspace? | `centralLogAnalyticsId` | `az monitor log-analytics workspace show --ids` returns 200 |
| BYO Azure OpenAI? | `existingOpenAiResourceId` | `az cognitiveservices account show --ids` + kind in (OpenAI, AIServices) |
| External ACR? | `externalAcrName` + `externalAcrResourceGroup` | `az acr show -n` returns 200; deployer/MI has AcrPull |
| Entra admin group OID (mandatory) | `aadAdminObjectId` + `aadAdminEmail` | `az ad group show --group` returns 200; email regex-validated |

> If `enablePrivateEndpoints=true` and BOTH `byoVnetPeSubnetId` and
> `peSubnetAddressPrefix` are unset, the Bicep `alz-assert-pe-subnet`
> guard fails the deployment at template-evaluation time.

**Manual equivalent** — pass every captured value as a Bicep parameter in
Phase 3.

---

## Phase 2 — AI service resolution

**Skill does automatically**: for each "Use Existing" selection, validates
the resource, verifies deployments exist (`gpt-5.4`, `gpt-5.4-mini`,
`text-embedding-3-small`), and runs a connectivity test against each
endpoint.

**Manual equivalent**:

```bash
# Verify OpenAI deployments exist:
az cognitiveservices account deployment list \
    --name "$OPENAI_NAME" --resource-group "$OPENAI_RG" \
    --query "[].{name:name, model:properties.model.name}" -o table

# Validate model names match Bicep params:
#   chatDeploymentName    → must equal the 'name' of a gpt-5.4 deployment
#   embeddingDeploymentName → must equal the 'name' of a text-embedding-3-small deployment
# Mismatches surface later as "DeploymentNotFound" during Phase 5 Step 4.
#
# Also verify embeddingModelNameParam + embeddingDimensions match the actual
# deployed embedding model. These become Embedding__ModelName / Embedding__Dimensions
# in the container and drive the search index vector-field schema. A dim mismatch
# silently breaks vector search (see FIX_SelfhostedVectorDimsConfigurable).
#   text-embedding-3-small / ada-002 → 1536
#   text-embedding-3-large           → 3072
```

---

## Phase 3 — Deploy (Bicep what-if → apply)

**Skill does automatically**:

1. Pre-grants `Key Vault Secrets Officer` to the deployer on the target KV
   (PARTNER_DEPLOY_HARDENING Rule 4 — avoids 403 on first Phase 3 secret write).
2. Runs `az deployment group what-if` — displays the change summary.
3. Prompts for `--auto-approve` or confirmation.
4. Applies the deployment.
5. Generates the SuperAdmin password + JWT secret + bootstrap API key with
   `System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)` and
   writes them to Key Vault immediately (no shell history).

**Manual Bicep equivalent** (enterprise):

```bash
cd selfhosted/infrastructure
pwsh ./selfhosted-deploy.ps1 \
    -ResourceGroup "$RG" \
    -Location "$LOCATION" \
    -Prefix "$PREFIX" \
    -Enterprise \
    -AadAdminObjectId "$AAD_ADMIN_OID" \
    -AadAdminEmail "$AAD_ADMIN_EMAIL" \
    -DeployerObjectId "$(az ad signed-in-user show --query id -o tsv)" \
    -ByoVnetSubnetId "$BYO_VNET_SUBNET" \
    -ByoVnetPeSubnetId "$BYO_PE_SUBNET" \
    -ByoKeyVaultId "$BYO_KV" \
    -CentralLogAnalyticsId "$BYO_LAW" \
    -ExistingOpenAiResourceId "$BYO_OPENAI"
```

---

## Phase 4 — Post-deploy (GHCR PAT, revisions)

### Step 4.1 — GHCR pull PAT (conditional)

**Runs only if external ACR was NOT selected in Phase 1.5.**

Mint a fine-grained GitHub PAT with `read:packages` scope and a 90-day
expiry. Store it in Key Vault as `ghcr--pull--token` with rotation tags:

```bash
EXPIRES_AT="$(date -d '+90 days' -Iseconds 2>/dev/null || date -u -v+90d +%Y-%m-%dT%H:%M:%SZ)"
az keyvault secret set \
    --vault-name "$KV_NAME" \
    --name ghcr--pull--token \
    --value "$PAT" \
    --tags "expiresAt=$EXPIRES_AT" "scope=read:packages" "rotationInterval=90d"
```

### Step 4.2 — Restart container app revisions

```bash
for app in "${PREFIX}-api" "${PREFIX}-mcp" "${PREFIX}-web"; do
    az containerapp revision restart -n "$app" -g "$RG"
done
```

Revisions must be restarted so Container Apps picks up the new registry
credential — skipping this leaves revisions stuck at `Pending` with image
pull 401.

---

## Phase 5 — Smoke test

**Enterprise only. Fails the skill on non-zero exit** — resources remain
in place for post-mortem (Rule 2 of `SH_ENTERPRISE_SMOKE_TEST.md`).

Invokes `selfhosted/scripts/post-deploy-smoke.sh` — 9 steps (MD / PDF /
PNG seeds exercise OpenAI, DocIntel, and Vision respectively, ending with
a Data Protection canary):

1. Wait for `/api/bootstrap/status` → `{ready: true}` (60s timeout).
2. Read the first bootstrap API key from KV `SelfHosted--BootstrapApiKey`.
3. Upload 3 seed files (MD, PDF, PNG).
4. Poll each item until `enrichmentStatus == "Complete"` (120s per item).
5. Assert `briefSummary`, `tags`, and `chunks` are populated.
6. Semantic search returns each seed's id.
7. Assert `EnrichmentOutbox` has 0 Failed entries (falls back to `sqlcmd`
   if the admin endpoint returns 404).
8. **Data Protection canary**: encrypt → restart API revision → decrypt
   (VERIFY 12 in SH_ENTERPRISE_SMOKE_TEST). Skipped with a WARN if
   `RESOURCE_GROUP` env is unset.
9. `[9/9] SMOKE PASSED`; the cleanup trap deletes the 3 seeds.

**Manual equivalent**:

```bash
export RESOURCE_GROUP="$RG"
export API_APP_NAME="${PREFIX}-api"
# Optional SQL fallback if the admin outbox endpoint is not yet deployed:
# export SQL_CONNECTION_STRING="tcp:<server>,1433;Database=...;Authentication=Active Directory Default"

bash selfhosted/scripts/post-deploy-smoke.sh \
    "https://${API_FQDN}" "$KV_NAME"
```

---

## Phase 6 — Front Door private-link approval

**Enterprise only, conditional** (runs only if Front Door was deployed).
**Runs only after Phase 5 passes** — never enable traffic on a broken
stack (Rule 6 of `SH_ENTERPRISE_SMOKE_TEST.md`).

Invokes `selfhosted/infrastructure/post-deploy/approve-front-door-pl.ps1`:

- Polls the CAE for up to 180s for Pending FD shared-private-link
  connections.
- Approves each via `az network private-endpoint-connection approve`.
- Asserts every final status is `Approved`; any `Pending` /
  `Disconnected` / `Rejected` fails the skill.

**Manual equivalent**:

```bash
pwsh selfhosted/infrastructure/post-deploy/approve-front-door-pl.ps1 \
    -ResourceGroup "$RG"
```

---

## Troubleshooting Matrix

| Symptom | Root cause | Fix | Reference |
|---|---|---|---|
| AppGW / Front Door backend `Unknown` / `Unhealthy` | Missing private DNS zone for internal CAE (`*.azurecontainerapps.io`) | Create zone + VNet link; re-run `approve-front-door-pl.ps1` if PL Pending | Vault `aea9e699-ba9d-…` (2026-04 partner outage) |
| OpenAI 401 after deploy | MI role assignment propagation lag (up to 10 min) | Wait + retry; force refresh: `az containerapp revision restart -n ${PREFIX}-api -g $RG` | — |
| KV 403 on deploy | `Key Vault Secrets Officer` not pre-granted to deployer | Pass `--deployer-object-id` to the skill, or manually `az role assignment create --role "Key Vault Secrets Officer" --assignee-object-id $DEPLOYER_OID --scope $KV_ID`, wait 30s, retry | PARTNER_DEPLOY_HARDENING Rule 4 |
| `EnrichmentOutbox` Failed > 0 after smoke | Stuck enrichment (OpenAI quota / DocIntel tier / Vision outage) | `GET /api/v1/admin/enrichment/outbox?status=Failed` to inspect; query `EnrichmentActivityLogs` for root cause | Builder B §2.8 |
| Migration appears to hang | SQL DTU saturation | Scale to S2 temporarily (`az sql db update --edition Standard --service-objective S2`); retry | SH_ENTERPRISE_BICEP_HARDENING §3.1 |
| FD PL stuck `Pending` after smoke | Auto-approve script failed or never ran | Run `approve-front-door-pl.ps1` manually; verify CAE `privateEndpointConnections` | — |
| Smoke fails Step 4 (enrichment) | Model deployment name mismatch (Bicep param vs Azure deployment) | Compare `chatDeploymentName` / `embeddingDeploymentName` params to `az cognitiveservices account deployment list` output | SH_ENTERPRISE_BICEP_HARDENING §3.4 |
| Container pull 401 | GHCR PAT expired (> 90 days) | Mint new PAT, `az keyvault secret set --name ghcr--pull--token`, restart revisions | Day-1 rotation playbook below |
| Bootstrap never reaches `ready: true` (Smoke Step 1) | DbContext cannot reach SQL over private endpoint | `nslookup <sql-host>.database.windows.net` from inside the CAE; should return a 10.x IP | — |
| `alz-assert-pe-subnet` fails at template evaluation | Both `byoVnetPeSubnetId` and `peSubnetAddressPrefix` unset with PE enabled | Supply one of the two parameters; rerun | SH_ENTERPRISE_BICEP_HARDENING §3.2 |
| DP canary (Step 8) reports `plaintext mismatch` | DP key ring did not survive restart (blob/KV wiring broken) | Verify `PersistKeysToAzureBlobStorage` container + MI `Storage Blob Data Contributor`; verify `ProtectKeysWithAzureKeyVault` key ID | SH_ENTERPRISE_RUNTIME_RESILIENCE §4 |
| Alerts never fire | `aadAdminEmail` mismatch or action group disabled | `az monitor action-group show -n {prefix}-ag-critical -g $RG --query enabled` | Monitoring section below |

---

## Day-1 Rotation Playbook

Run immediately after smoke + FD approval pass.

### SuperAdmin password rotation

Two paths:

1. **UI** — log in with the bootstrapped password, open Profile → Change
   Password, supply a new one of 16+ chars. This is the preferred path.
2. **Key Vault** — update the `SelfHosted--SuperAdmin--Password` secret
   and restart the API revision:

   ```bash
   NEW_PASSWORD="$(openssl rand -base64 24)"
   az keyvault secret set \
       --vault-name "$KV_NAME" \
       --name SelfHosted--SuperAdmin--Password \
       --value "$NEW_PASSWORD"
   az containerapp revision restart -n "${PREFIX}-api" -g "$RG"
   ```

### JWT secret regeneration

The JWT secret must be ≥ 32 chars. Regenerate on any suspected
compromise — every active token is invalidated on restart.

```csharp
// In a scratch script / LINQPad; do NOT commit the output:
var secret = Convert.ToBase64String(
    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
Console.WriteLine(secret);
```

```bash
az keyvault secret set \
    --vault-name "$KV_NAME" \
    --name SelfHosted--JwtSecret \
    --value "$NEW_SECRET"

for app in "${PREFIX}-api" "${PREFIX}-mcp"; do
    az containerapp revision restart -n "$app" -g "$RG"
done
```

### GHCR PAT rotation (90-day cadence)

The `ghcr--pull--token` KV secret carries an `expiresAt` tag. Set a
calendar reminder for 14 days before that date.

```bash
# Verify current expiry:
az keyvault secret show \
    --vault-name "$KV_NAME" \
    --name ghcr--pull--token \
    --query 'tags.expiresAt' -o tsv

# Rotate:
NEW_PAT="ghp_...""  # mint in github.com/settings/tokens?type=beta
NEW_EXPIRY="$(date -d '+90 days' -Iseconds 2>/dev/null || date -u -v+90d +%Y-%m-%dT%H:%M:%SZ)"
az keyvault secret set \
    --vault-name "$KV_NAME" \
    --name ghcr--pull--token \
    --value "$NEW_PAT" \
    --tags "expiresAt=$NEW_EXPIRY" "scope=read:packages" "rotationInterval=90d"

for app in "${PREFIX}-api" "${PREFIX}-mcp" "${PREFIX}-web"; do
    az containerapp revision restart -n "$app" -g "$RG"
done
```

See also the `update-selfhosted` skill pattern in the repo-root `CLAUDE.md`.

### First-day verification checklist

1. `ghcr--pull--token` tag `expiresAt` is 88–92 days in the future.
2. `SelfHosted--BootstrapApiKey` has `attributes.enabled=false` after 24h
   (auto-expiry — Builder B §2.5).
3. Capture the system-assigned MI principal IDs for API, MCP, and (if
   deployed) Functions into the customer's identity inventory.
4. Schedule the smoke script to run quarterly as a CI job.

---

## Backup / Restore

### SQL — Long-Term Retention (LTR)

Enabled by default in the enterprise Bicep: 26 weekly backups + 1 yearly.

Restore via portal (`SQL database → Backups → Available backups`) or CLI:

```bash
az sql db ltr-backup list \
    --location "$LOCATION" \
    --resource-group "$RG" \
    --server "${PREFIX}-sql" \
    --database KnowzKnowledge -o table

az sql db ltr-backup restore \
    --dest-database KnowzKnowledge-restored \
    --dest-server "${PREFIX}-sql" \
    --dest-resource-group "$RG" \
    --backup-id "<ltr-backup-id>"
```

### Blob storage

Soft-delete enabled (14 days) + versioning.

```bash
az storage blob undelete \
    --account-name "${PREFIX}sa" \
    --container-name selfhosted-files \
    --name "<blob-path>" \
    --auth-mode login
```

### Key Vault

Purge protection + 90-day soft-delete by default.

```bash
az keyvault recover --name "$KV_NAME"
# For a purged (hard-deleted) vault: RESTORE IS NOT POSSIBLE — only
# backup/restore via `az keyvault backup` before purge helps.
```

### Data Protection key ring

Already blob-persisted + KV-wrapped (SEC_P0Triage finding #12). Rotation
is automatic; see `SH_ENTERPRISE_RUNTIME_RESILIENCE.md` §4.

### Application-level export

No application-level export today. Use the API data-portability
tools (`docs/DATA_PORTABILITY.md`).

---

## MI Rotation Policy (180-day cadence)

System-assigned MI rotation is automatic — Azure AD handles the
credential lifecycle and federated identity tokens. No client secrets to
rotate (UAMI uses federated identity).

**Every 180 days**:

1. Review UAMI identity principals and role assignments:

   ```bash
   for mi in $(az identity list -g "$RG" --query '[].principalId' -o tsv); do
       echo "MI $mi:"
       az role assignment list --assignee "$mi" -o table
   done
   ```

2. Confirm no stale role assignments point at deleted MIs — list
   assignments scoped to `$RG` and spot-check each `principalId` resolves.
3. Audit the `SH_ENTERPRISE_MI_SWAP.md` Rule 4 role list against current
   assignments — if migrating to user-assigned MI, schedule a maintenance
   window, reassign all 5 role assignments, restart revisions, rerun
   smoke.

---

## Monitoring & alerts

Alerts are deployed separately from the main enterprise template so
customers can bring their own alerting stack:

```bash
az deployment group create \
    --resource-group "$RG" \
    --template-file selfhosted/infrastructure/selfhosted-enterprise-alerts.bicep \
    --parameters prefix="$PREFIX" \
                 aadAdminEmail="$AAD_ADMIN_EMAIL" \
                 sqlServerName="${PREFIX}-sql" \
                 sqlDatabaseName=KnowzKnowledge \
                 containerAppEnvironmentName="${PREFIX}-cae" \
                 logAnalyticsWorkspaceId="$LAW_ID"
```

Configured alerts:

| Alert | Severity | Trigger |
|---|---|---|
| `{prefix}-critical-logs` | 1 | Any `Critical` ContainerAppConsoleLogs entry in 5 min |
| `{prefix}-sql-dtu` | 2 | SQL DTU consumption > 80% averaged over 10 min |
| `{prefix}-outbox-failure-rate` | 2 | EnrichmentOutbox failure rate > 5% over 15 min |

Notifications go to the customer's `aadAdminEmail` only — Knowz Ops is
NOT subscribed by default (Rule 6 of `SH_ENTERPRISE_SKILL.md`).

Thresholds are defaults (spec §4 Debt D4). Customers can override by
redeploying the alerts template with tuned thresholds.

---

## Cross-reference: enterprise spec set

All nine specs below form the source-of-truth set for this runbook.

| Spec | Scope |
|---|---|
| `knowzcode/specs/SH_ENTERPRISE_DEPLOY.md` | Infrastructure topology + divergences from standard |
| `knowzcode/specs/SH_ENTERPRISE_BICEP_HARDENING.md` | Bicep guards (PE subnet, DTU tier, MI-only SQL, Front Door PL) |
| `knowzcode/specs/SH_ENTERPRISE_BYO_INFRA.md` | BYO VNet / KV / LAW / OpenAI / ACR capture + validation |
| `knowzcode/specs/SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP.md` | First-boot SuperAdmin / JWT / bootstrap API key minting |
| `knowzcode/specs/SH_ENTERPRISE_MI_SWAP.md` | Managed Identity role assignments (Rule 4 role list) |
| `knowzcode/specs/SH_ENTERPRISE_RUNTIME_RESILIENCE.md` | Data Protection, enrichment outbox, startup validator |
| `knowzcode/specs/SH_ENTERPRISE_SECURITY_HARDENING.md` | SEC_P0Triage findings (DP key ring, secrets handling) |
| `knowzcode/specs/SH_ENTERPRISE_SKILL.md` | Skill UX + this runbook spec (§2.2) + alerts spec (§2.3) |
| `knowzcode/specs/SH_ENTERPRISE_SMOKE_TEST.md` | Post-deploy smoke 9-step flow + failure rules |
| `knowzcode/specs/PARTNER_DEPLOY_HARDENING.md` | Deployer pre-grant (Rule 4), generic hardening lessons |
