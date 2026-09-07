# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| Latest release | Yes |
| Previous releases | Security fixes only |

## Reporting a Vulnerability

**Do NOT open a public GitHub issue for security vulnerabilities.**

If you discover a security vulnerability in Knowz Self-Hosted, please report it responsibly by emailing:

**security@knowz.io**

Include the following in your report:

- Description of the vulnerability
- Steps to reproduce the issue
- Affected version(s)
- Potential impact assessment
- Any suggested fix (optional)

## Response Timeline

| Stage | Timeframe |
|-------|-----------|
| Acknowledgment | Within 48 hours |
| Initial assessment | Within 7 days |
| Fix development | Depends on severity |
| Security advisory | Published with fix release |

We will work with you to understand the scope and impact of the issue before any public disclosure.

## Responsible Disclosure

We ask that you:

- Allow us reasonable time to investigate and address the issue before public disclosure
- Avoid exploiting the vulnerability beyond what is necessary to demonstrate the issue
- Do not access or modify other users' data
- Act in good faith to avoid disruption to running services

## Scope

The following are **in scope** for this security policy:

- Knowz Self-Hosted API server
- Knowz Self-Hosted web application
- Knowz MCP server
- Docker images published under this project
- Dependencies bundled with the project

The following are **out of scope**:

- The Knowz cloud platform (https://knowz.io) -- this has a separate security policy
- Third-party services (Azure OpenAI, Azure AI Search) configured by the user
- Issues in upstream dependencies (report those to the respective maintainers, but let us know so we can update)

## Security Best Practices for Deployment

When deploying Knowz Self-Hosted in production:

- Change the default admin password immediately after first login
- Use a strong, unique `JWT_SECRET` (minimum 32 characters)
- Use a strong `POSTGRES_PASSWORD` (do not reuse the admin password)
- Restrict `SelfHosted__AllowedOrigins` to your actual domain
- Set `ALLOWED_HOSTS` to that hostname when the API or MCP is reached by a custom `Host` header (compose default is `localhost;127.0.0.1;[::1]` plus the container DNS name — not `*`)
- Leave Swagger off (`ENABLE_SWAGGER` unset or `false`); enable only on a locked-down debug host
- Do not publish the database host port. Compose keeps Postgres on the internal network. If a host-side tool needs it, bind loopback only (`127.0.0.1:${DB_PORT:-5432}:5432`)
- Place the stack behind a reverse proxy with TLS termination
- Keep Docker images updated to the latest release
- Review and restrict network access to exposed ports
