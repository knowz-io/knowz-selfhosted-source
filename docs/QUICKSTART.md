# Quickstart Guide

This guide targets the **Knowz CLI 0.5.0 preview**. CLI versions and selfhosted image versions are separate. `knowz up` opens local browser setup, pulls published GHCR **release images**, and starts the web UI. A standard installation requires no source checkout or build.

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) or Docker Engine with the Compose plugin, running on this computer
- [Node.js 22+](https://nodejs.org/) for the npm install below
- Space and memory for PostgreSQL, API, web and MCP containers; a locally hosted AI model needs its own resources

Node-free installers and checksums are available through [Knowz CLI releases](https://github.com/knowz-io/knowz-cli/releases). The 0.5.0 native preview is unsigned and not notarized; check the status of the exact artifact you download.

## Step 1: Open setup

Run the exact preview version:

```bash
npx @knowzai/cli@0.5.0 up
```

Or install it once:

```bash
npm i -g @knowzai/cli@0.5.0
knowz up
```

The browser guides you through the AI provider, services, administrator account and Review. Selfhosted is the default edition and needs no license or GitHub login. Choose no AI provider to begin with local capture and keyword search; add a provider later when you need AI answers.

Setup pulls `ghcr.io/knowz-io/knowz-selfhosted-api`, `knowz-selfhosted-web`, and `knowz-selfhosted-mcp` at the selected complete release's digests. Its private runtime directory is `~/.knowz/selfhosted/knowz/` by default, or `~/.knowz/selfhosted/<name>/` with `--name`. `KNOWZ_CONFIG_DIR` can relocate that root.

For a separate installation or occupied ports:

```bash
knowz up --name notes --api-port 15000 --web-port 13000 --mcp-port 13001
```

Use the same `--name notes` on subsequent setup, status, stop, backup and upgrade commands. The [public Apache-2.0 source](https://github.com/knowz-io/knowz-selfhosted-source) is also available for customization; see [Compose from a checkout](#compose-from-a-checkout-operators-only).

## Step 2: Start the services

Review the settings and start. Progress remains available while images download and services start. You can cancel or retry from the setup page. On a fresh installation, the API applies database migrations and creates the first administrator (`admin`).

Use the service URLs shown by setup. The defaults are:

| Service | Default URL or internal address |
|---------|---------------------------------|
| Web UI | [http://localhost:3000](http://localhost:3000) |
| API | [http://localhost:5000](http://localhost:5000) |
| MCP | [http://localhost:3001/mcp](http://localhost:3001/mcp) |
| PostgreSQL + pgvector | `db:5432` internally; **not published on the host** |

Both CLI-managed and source Compose installations keep PostgreSQL on the container network. An existing host PostgreSQL server does not conflict with this default. Remapped service ports are reflected in setup's URLs.

## Step 3: Finish sign-in

If you left the password blank, setup shows the generated temporary password once: choose **Show password** or **Copy password**. Sign in to the Web URL as `admin` with that temporary password and change it to a permanent password before continuing. If you supplied a password during setup, use that password and follow any required password-change step.

Return to the setup page's **Connect CLI** form and use the current password. You can also complete a required password change there by entering both the current and new passwords. **Finish signing in** means services started but CLI authentication still needs attention; it is not a reason to reinstall. A wrong password leaves the same runtime available for retry.

Generated credentials are not included in routine logs or replayed after refresh. If you missed the one-time display, the private runtime `.env` holds the original bootstrap `ADMIN_USERNAME` and `ADMIN_PASSWORD`. After a password change, that bootstrap password is no longer the account's current password; editing `.env` does not reset the account.

For an existing account, terminal sign-in prompts for the password without displaying it:

```bash
knowz login --self-hosted --username admin --api-url http://localhost:5000 --profile local
```

Use the exact API URL displayed by setup. The default runtime uses profile `local`; a named runtime uses `local-<name>` (for example `local-notes`). Credentials are bound to that endpoint and profile. Successful sign-in verifies and stores the per-user API key; it does not silently replace an existing key that you already use elsewhere.

## First steps

1. **Create a vault** to organize related knowledge.
2. **Create a note**, then attach a supported text file.
3. **Search for a distinctive phrase** from your note or attachment. Keyword search works without AI; semantic search requires an embedding provider.
4. **Use CLI or MCP** with the same instance's per-user API key. Settings → API Keys manages programmatic access.

Without a provider, Ask and Chat explain that AI is unavailable instead of inventing an answer. Capture, files and keyword search remain usable.

## Add or change an AI provider

Reopen setup for the existing installation:

```bash
knowz setup
# Named installation:
knowz setup --name notes
```

Choose **Apply my changes (reconfigure)** when updating an existing installation. Select an OpenAI-compatible endpoint (including a local model server), Azure OpenAI, or an optional connection to Knowz Cloud. Configure the chat and embedding models together; embedding dimensions must match the selected model. Azure AI Search is optional: local PostgreSQL + pgvector supplies search without that service. See the [Configuration Reference](CONFIGURATION.md) for provider details.

Administrator **Configuration** shows supported provider settings, their authority and whether a restart is required. Managed secrets stay read-only there; update them through host setup or the managed secret store indicated by the UI. **Saved** does not mean **active** until any required API restart has completed. A configuration-only check is not proof of authenticated provider connectivity. After a browser configuration save requires restart, run `knowz down` then `knowz up` for the same instance to load it. Browser **Settings → Connection** changes which Knowz server this browser uses; it does not configure an AI provider.

## Stopping, restarting and recovering

```bash
# Inspect actual service health and the recorded version
knowz runtime status --edition selfhosted

# Stop services; preserve database, files and protection keys
knowz down --edition selfhosted

# Start or reconnect to this installation
knowz up

# Diagnose a failed start
knowz runtime doctor --edition selfhosted
knowz runtime logs --edition selfhosted --tail 100
```

Add the same `--name` for a named installation. `knowz setup --name notes` can inspect the existing runtime, start it if stopped, and retry sign-in with the current password. A browser pointed at an unreachable saved server can reset its connection to this instance from the login page; that resets browser connection state, not server data.

## Upgrading

Take an encrypted backup, then select a compatible **selfhosted image version**, independently of the CLI version:

```bash
knowz backup --edition selfhosted --encrypt --out knowz-before-upgrade.tar.gz.enc
knowz runtime upgrade --edition selfhosted --version 0.16.2
knowz runtime status --edition selfhosted
```

The terminal asks for a hidden backup passphrase; retain it separately from the archive. Add the same `--name` throughout when applicable. Upgrade preserves database, file and protection-key volumes, and applies required database migrations on startup. Use a pre-upgrade backup for recovery into a separate stopped target; do not point an older image at a newer database and assume schema compatibility.

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

Log in at [http://localhost:3000](http://localhost:3000) with `ADMIN_USERNAME` / `ADMIN_PASSWORD` and complete any required password change. Stop with `docker compose down` (volumes preserved). For this source-build path, review the new source revision and rebuild with `docker compose up --build -d`.

## Troubleshooting

### CLI install

If `npx @knowzai/cli@0.5.0 up` or `knowz up` fails, confirm Docker is running and Node is 22+ for the npm install. Use `knowz runtime doctor --edition selfhosted` and the reported setup error. Remap occupied ports with `--api-port`, `--web-port`, and `--mcp-port`; use `--name` to identify a separate installation. The CLI reports the bound service URLs.

### Port Conflicts

If you are on the operator compose path and ports 3000, 3001, or 5000 are already in use, either stop the conflicting service or change the port mapping in `docker-compose.yml`:

```yaml
ports:
  - "3001:8080"  # Change the left side (host port) to an available port
```

The database does not occupy a host port in this release. Existing older installations can retain a historical loopback database mapping until their runtime configuration is updated.

### Postgres Memory

`pgvector/pgvector:pg16` is a light image. If the `db` container exits immediately, check Docker Desktop memory allocation under Settings > Resources. Host port 5432 is not published by default, so a local Postgres install does not collide.

### API Fails to Start

Check the API logs for details:

```bash
docker compose logs api
```

Common issues:
- **Database connection failed** -- The Postgres container may still be starting. The API retries automatically with exponential backoff (up to 10 attempts).
- **Migration error** -- Preserve the database and inspect the migration failure. Recover from a verified backup into a separate target when needed; removing volumes deletes the data.

### ARM64 (Apple Silicon) Notes

`pgvector/pgvector:pg16` is multi-arch. No Rosetta emulation is required.

### Viewing Logs

```bash
# All services
docker compose logs -f

# Specific service
docker compose logs -f api
```
