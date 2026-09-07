# Terraform — Enterprise

> **Not offered in this snapshot.** This guide describes the Microsoft SQL-era Azure deployment. The current self-hosted API runs on PostgreSQL + pgvector and these templates are incompatible with it. Retained for reference and for a future update. Use Docker Compose or the `knowz` CLI.

## What is here

These templates provision the Microsoft SQL-era Azure topology for Knowz Self-Hosted. They are **not** wired to the current API, which is Npgsql-only (PostgreSQL 16 + pgvector, database `knowz_selfhosted`). Running them today produces a portal experience that provisions Microsoft SQL and an API container that cannot start against it.

They are kept unchanged so that the future conversion to Azure Database for PostgreSQL Flexible Server 16 with the `vector` extension starts from a known baseline. Do not run `terraform apply`, `az deployment group create`, or the deploy scripts against them expecting a working stack.

## Supported deployment paths

- The `knowz` CLI — `npx @knowzai/cli up` / `knowz up` (customer install; public GHCR images).
- Docker Compose from a checkout — operators only; see the repository [README](../../README.md).
