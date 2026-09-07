# Contributing to Knowz Self-Hosted

Thank you for your interest in contributing to Knowz Self-Hosted. This guide covers everything you need to get started.

## Ways to Contribute

- **Bug reports** -- Open an issue with steps to reproduce, expected behavior, and actual behavior
- **Feature requests** -- Open an issue describing the use case and proposed solution
- **Documentation** -- Fix typos, improve explanations, add examples
- **Code** -- Bug fixes, new features, performance improvements

## Development Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| .NET SDK | 10.0+ | Backend API, MCP server, AppHost, Setup wizard |
| Node.js | 22+ | Frontend web application |
| Docker Desktop | Latest | Running the PostgreSQL + pgvector container and the full stack |
| Git | Latest | Version control |

## Building from Source

Customers should install with `npx @knowzai/cli` / `knowz up` — see the [README](README.md). That path pulls public GHCR images and does not need this repository.

The reviewed Apache-2.0 source is public at [knowz-selfhosted-source](https://github.com/knowz-io/knowz-selfhosted-source). Its history begins with the reviewed standalone snapshot; proprietary platform code and private build history are excluded.

### Backend (API and MCP)

```bash
git clone https://github.com/knowz-io/knowz-selfhosted-source.git
cd knowz-selfhosted-source

# Restore and build
dotnet build Knowz.SelfHosted.sln

# Run tests
dotnet test src/Knowz.SelfHosted.Tests
```

### Frontend (Web)

```bash
cd src/knowz-selfhosted-web

# Install dependencies
npm ci

# Start development server
npm run dev
```

### Full Stack (Docker)

```bash
# Generate the required credentials and Compose configuration
dotnet run --project src/Knowz.SelfHosted.Setup

# Build and run all services
docker compose up --build
```

## Pull Request Process

1. **Fork** the repository and create a feature branch from `main`
2. **Implement** your changes with appropriate tests
3. **Test** locally -- ensure `dotnet test` passes and the Docker stack starts cleanly
4. **Commit** using conventional commit messages (see below)
5. **Push** your branch and open a pull request against `main`
6. **Describe** your changes in the PR body -- what changed, why, and how to test it

### PR Requirements

- All existing tests must pass
- New features should include tests
- Breaking changes must be documented
- The Docker Compose stack must start and pass health checks

## Commit Message Format

This project uses [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add vault export to CSV format
fix: resolve file upload timeout for large documents
docs: update QUICKSTART with ARM64 notes
refactor: simplify enrichment pipeline retry logic
test: add integration tests for search endpoints
```

**Types:** `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `ci`

## Coding Standards

### C# (.NET)

- Follow [Microsoft C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use nullable reference types (`#nullable enable`)
- Prefer async/await for I/O operations
- Use minimal API endpoint style (not controllers)

### TypeScript / React

- Use TypeScript strict mode
- Prefer functional components with hooks
- Use named exports

### General

- Keep changes focused -- one concern per PR
- Avoid large formatting-only changes mixed with logic changes
- Write code that is readable without excessive comments

## Project Structure

```
Knowz.SelfHosted.sln
src/
  Knowz.Core/                      # Shared domain models and interfaces
  Knowz.SelfHosted.API/            # Self-hosted API server (.NET 10)
  Knowz.SelfHosted.Application/    # Application services and business logic
  Knowz.SelfHosted.Infrastructure/ # Data access, search, file storage
  Knowz.SelfHosted.AppHost/        # Aspire orchestrator for local development
  Knowz.SelfHosted.Setup/          # Interactive Spectre.Console setup wizard
  Knowz.SelfHosted.Tests/          # Integration and unit tests
  Knowz.MCP/                       # Model Context Protocol server
  Knowz.MCP.Tests/                 # MCP unit tests
  knowz-selfhosted-web/            # React frontend (TypeScript)
```

## Reporting Issues

When reporting a bug, please include:

- Steps to reproduce the issue
- Expected vs. actual behavior
- Docker Compose logs if relevant (`docker compose logs <service>`)
- Environment details (OS, Docker version, browser)

For security vulnerabilities, see [SECURITY.md](SECURITY.md) instead of opening a public issue.

## License

By contributing to this project, you agree that your contributions will be licensed under the [Apache License 2.0](LICENSE).
