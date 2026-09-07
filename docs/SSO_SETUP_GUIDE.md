# SSO Setup Guide: Microsoft Entra ID

This guide walks through configuring Single Sign-On (SSO) for Knowz Self-Hosted using Microsoft Entra ID (formerly Azure AD). SSO allows your organization's users to sign in with their Microsoft work accounts instead of separate usernames and passwords.

## Two Authentication Modes

Knowz supports two SSO modes. The mode is **auto-detected** based on whether you provide a client secret.

### PKCE Public Client Mode (no client secret)

| | |
|---|---|
| **You provide** | Client ID + Directory Tenant ID |
| **Azure app type** | Public client (SPA) |
| **Security model** | PKCE code challenge is the only proof mechanism. The `tid` (tenant ID) claim in the ID token is validated against your configured Directory Tenant ID(s) to restrict which organizations can sign in. |
| **Best for** | Single-organization deployments where you want the simplest possible setup. No secrets to rotate. |
| **Limitation** | Directory Tenant ID is **required** — without it AND without a client secret, SSO is disabled (there's no security boundary). Cannot use the `/common` endpoint (would allow any Microsoft account). |

**How it works:** The browser sends a PKCE `code_challenge` with the authorization request. After the user authenticates, the server exchanges the authorization code using the `code_verifier` (proof of the original challenge). No client secret is transmitted. The server then validates the `tid` claim in the ID token against your allowed tenant ID list.

### Confidential Client Mode (with client secret)

| | |
|---|---|
| **You provide** | Client ID + Client Secret + (optional) Directory Tenant ID |
| **Azure app type** | Web application (confidential) |
| **Security model** | Server-to-server authentication using the client secret, plus PKCE as defense-in-depth. The client secret proves the application's identity to Microsoft during token exchange. |
| **Best for** | Multi-organization deployments, or when your security policy requires server-side application authentication. Supports restricting to specific tenants OR allowing any Microsoft account. |
| **Trade-off** | You must create and periodically rotate a client secret. |

**How it works:** Same flow as PKCE mode, but the token exchange additionally includes the `client_secret` parameter. This means even if an attacker intercepts the authorization code, they cannot exchange it without the secret (which only lives on the server). If Directory Tenant ID is set, the `tid` claim is additionally validated. If not set, the `/common` endpoint is used and any Microsoft account can attempt to sign in (user must still exist or auto-provisioning must be enabled).

### Decision Matrix

| Scenario | Recommended Mode |
|----------|-----------------|
| Single company, simple setup | PKCE Public Client |
| Multiple organizations need access | Confidential Client + CSV Tenant IDs |
| Security policy requires app-level auth | Confidential Client |
| You want any Microsoft user to sign in | Confidential Client (no Tenant ID) + Auto-Provision |
| You don't want to manage secrets | PKCE Public Client |

---

## Step 1: Register an App in Azure Entra ID

### For PKCE Public Client Mode

1. Go to [Azure Portal](https://portal.azure.com) > **Microsoft Entra ID** > **App registrations** > **New registration**
2. **Name:** `Knowz Self-Hosted SSO` (or any descriptive name)
3. **Supported account types:** Select **"Accounts in this organizational directory only"** (single tenant)
4. **Redirect URI:**
   - Platform: **Single-page application (SPA)**
   - URI: `https://your-knowz-domain.com/auth/sso/callback`
   - For local dev: `http://localhost:3000/auth/sso/callback`
5. Click **Register**
6. Note the **Application (client) ID** from the Overview page
7. Note the **Directory (tenant) ID** from the Overview page
8. Go to **Authentication** > scroll to **Advanced settings** > set **Allow public client flows** to **Yes** > **Save**

**No client secret is needed for this mode.**

### For Confidential Client Mode

1. Go to [Azure Portal](https://portal.azure.com) > **Microsoft Entra ID** > **App registrations** > **New registration**
2. **Name:** `Knowz Self-Hosted SSO`
3. **Supported account types:**
   - **Single org:** "Accounts in this organizational directory only"
   - **Multi-org:** "Accounts in any organizational directory"
   - **Any Microsoft account:** "Accounts in any organizational directory and personal Microsoft accounts"
4. **Redirect URI:**
   - Platform: **Web**
   - URI: `https://your-knowz-domain.com/auth/sso/callback`
5. Click **Register**
6. Note the **Application (client) ID**
7. Note the **Directory (tenant) ID** (optional for this mode, but recommended)
8. Go to **Certificates & secrets** > **New client secret**
   - Description: `Knowz SSO`
   - Expiry: Choose based on your rotation policy (recommended: 6-12 months)
9. **Copy the secret Value immediately** (it's only shown once)

---

## Step 2: Configure SSO in Knowz

### Option A: Admin UI (Recommended)

1. Log in to Knowz as a **SuperAdmin** user
2. Navigate to **Admin > SSO** in the sidebar
3. Fill in the form:

   | Field | PKCE Mode | Confidential Mode |
   |-------|-----------|-------------------|
   | **Enable SSO** | Checked | Checked |
   | **Application (Client) ID** | From Azure portal | From Azure portal |
   | **Client Secret** | Leave empty | Paste secret value |
   | **Directory (Tenant) ID(s)** | Required — paste your Entra tenant ID | Optional — paste tenant ID to restrict access |
   | **Auto-Provision Users** | Your choice | Your choice |
   | **Default Role** | `User` (recommended) | `User` (recommended) |

4. Click **Test Connection** to verify the OIDC discovery endpoint is reachable
5. Click **Save Configuration**
6. The login page will now show a **"Sign in with Microsoft"** button

### Option B: API

```bash
curl -X PUT "https://your-knowz-domain.com/api/v1/sso/config" \
  -H "Authorization: Bearer <superadmin-jwt>" \
  -H "Content-Type: application/json" \
  -d '{
    "isEnabled": true,
    "clientId": "00000000-0000-0000-0000-000000000000",
    "clientSecret": null,
    "directoryTenantId": "11111111-1111-1111-1111-111111111111",
    "autoProvisionUsers": true,
    "defaultRole": "User"
  }'
```

For confidential mode, include `"clientSecret": "your-secret-value"`.

### Option C: Environment Variables (Docker Compose)

Add to your `docker-compose.yml` under the API service environment:

```yaml
environment:
  SSO__Enabled: "true"
  SSO__Microsoft__ClientId: "00000000-0000-0000-0000-000000000000"
  SSO__Microsoft__DirectoryTenantId: "11111111-1111-1111-1111-111111111111"
  # SSO__Microsoft__ClientSecret: "your-secret"  # Only for confidential mode
  SSO__AutoProvisionUsers: "true"
  SSO__DefaultRole: "User"
```

Note: Admin UI configuration (stored in the database) takes priority over environment variables. If you configure SSO via the Admin UI, those settings override environment variables.

---

## Step 3: User Access Control

### Auto-Provisioning

When **Auto-Provision Users** is enabled:
- Any user from the allowed Entra tenant(s) who successfully authenticates via Microsoft will automatically get a Knowz account
- The account is created with the **Default Role** you configured (typically `User`)
- Their email, display name, and Entra subject ID are stored for future logins
- SSO-only users cannot log in with a password

When **Auto-Provision Users** is disabled:
- A SuperAdmin must pre-create user accounts with matching email addresses before those users can sign in via SSO
- On first SSO login, the existing account is linked to the Entra identity (OAuthSubjectId is stored)
- Users without a matching account see: "No account found. Registration is disabled for SSO users."

### User Matching Logic

On each SSO login, Knowz matches the user in this order:

1. **By OAuth Subject ID + Provider** — Most reliable. Survives email changes in Entra.
2. **By Email** — Matches existing password-based accounts to link them to SSO. On first match, the OAuth identity is stored for future logins.
3. **Auto-Provision** — If enabled and no match found, creates a new account.

### Multi-Organization Access

You can allow users from multiple Entra tenants by entering comma-separated Directory Tenant IDs:

```
11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222
```

When multiple tenant IDs are configured:
- The Microsoft `/common` endpoint is used (allows any org to attempt login)
- The `tid` claim in each ID token is validated against your allowed list
- Users from unlisted tenants are rejected

When a single tenant ID is configured:
- The tenant-specific endpoint is used (slightly faster OIDC discovery)
- Only users from that specific tenant can authenticate

---

## Troubleshooting

### "SSO is not enabled"

The `SSO:Enabled` setting is `false`. Go to Admin > SSO and enable it.

### "SSO not configured for Microsoft"

No `Client ID` has been set. Check your SSO configuration.

### "SSO configuration incomplete"

The mode could not be determined. This happens when:
- **No client secret AND no Directory Tenant ID** — The system cannot operate securely in either mode. Provide at least one.
- **Confidential mode but secret is missing** — A client ID is set with no secret and no tenant ID.

### "Token exchange failed"

The authorization code could not be exchanged for tokens. Common causes:
- **Redirect URI mismatch** — The redirect URI in Azure must exactly match `https://your-domain/auth/sso/callback`
- **Expired code** — Authorization codes are single-use and expire quickly. Try again.
- **Wrong client secret** — Double-check the secret value in SSO configuration.
- **PKCE mode with Web platform** — If using PKCE mode, the Azure app must use **SPA** platform, not **Web**.

### "ID token validation failed"

The ID token signature or claims could not be verified. Check:
- **Clock skew** — Ensure your server's clock is synchronized (within 5 minutes)
- **Client ID mismatch** — The token's audience must match your configured Client ID
- **Nonce mismatch** — Usually indicates a replay attack or stale state

### "Your organization is not authorized for SSO access"

The `tid` claim in the ID token doesn't match any configured Directory Tenant ID. The user is from a different Entra tenant than the one(s) you've allowed.

### "Invalid or expired state"

The SSO flow took longer than 10 minutes, or the server restarted during the flow. The user should try again. (State is stored in-memory with a 10-minute TTL.)

### "No account found. Registration is disabled for SSO users."

A user authenticated successfully via Microsoft, but:
- No Knowz account exists with their email address
- Auto-Provision Users is disabled

Either enable auto-provisioning or pre-create a user account with the matching email.

### SSO button doesn't appear on login page

- SSO is not enabled (`SSO:Enabled` is `false`)
- No provider is configured (Client ID is empty)
- The detected mode is `Disabled` — check that you've provided either a client secret or a Directory Tenant ID alongside the Client ID

---

## API Reference

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/v1/auth/sso/providers` | GET | Anonymous | List enabled SSO providers |
| `/api/v1/auth/sso/authorize` | GET | Anonymous | Generate authorization URL (`?provider=Microsoft&redirectUri=...`) |
| `/api/v1/auth/sso/callback` | POST | Anonymous | Exchange code for JWT (`{code, state}`) |
| `/api/v1/sso/config` | GET | SuperAdmin | Get current SSO configuration |
| `/api/v1/sso/config` | PUT | SuperAdmin | Update SSO configuration |
| `/api/v1/sso/config` | DELETE | SuperAdmin | Clear all SSO configuration |
| `/api/v1/sso/config/test` | POST | SuperAdmin | Test OIDC discovery connectivity |
| `/api/v1/sso/config/mode` | GET | SuperAdmin | Get detected SSO mode |

---

## Security Notes

- **PKCE (S256)** is used in both modes — it's always-on, not optional
- **State parameter** is cryptographically random, single-use, and expires after 10 minutes (prevents CSRF)
- **Nonce** is validated in the ID token (prevents replay attacks)
- **ID token signatures** are validated using JWKS from the OIDC discovery endpoint
- **Client secrets** are stored encrypted in the database (Data Protection API)
- **SSO-only users** have `PasswordHash = "SSO_ONLY_NO_PASSWORD"` — this is not valid BCrypt and will never match password verification
- **In-memory state** does not survive API restarts — if the server restarts during an SSO flow, the user must retry (not a security issue, just UX)
