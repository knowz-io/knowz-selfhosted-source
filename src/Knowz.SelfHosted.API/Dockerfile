# anydoc CLI (local document -> GFM conversion). WorkGroupID: kc-feat-anydoc-portable-tools-20260913-152433,
# NodeID: INFRA_AnydocImageBundling (R2/R3/R4). The version pin is deliberate and must stay
# character-identical to ANYDOC_VERSION in scripts/portable/ensure-host-tools.{sh,ps1} so the container
# path and the host path resolve the same tool; floating ranges (^, ~, latest) are forbidden.
# node:20-bookworm-slim is digest-pinned to its multi-arch manifest-list (resolved 2026-09-13). bookworm
# (glibc 2.36) is deliberate: the dotnet/aspnet:10.0 runtime moved to Trixie and glibc is backward
# compatible in that direction. Refresh: docker buildx imagetools inspect node:20-bookworm-slim
FROM node:20-bookworm-slim@sha256:2cf067cfed83d5ea958367df9f966191a942351a2df77d6f0193e162b5febfc0 AS anydoc
RUN npm install -g --no-audit --no-fund @firecrawl/anydoc@0.2.4

# Base stage: ASP.NET 10.0 runtime
# Base images pinned by digest (multi-arch manifest-list digests, supply-chain integrity).
# Refresh: docker buildx imagetools inspect <image>:<tag>  (update tag + digest together).
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597 AS base
WORKDIR /app

# Build stage: .NET SDK for compilation
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /source

# The reviewed public export contains source projects, not private package binaries.
# Keep an empty first-party feed so nuget.config still blocks dependency confusion.
COPY ["nuget.config", "."]
RUN mkdir -p local-packages

# Copy project files for NuGet restore caching
COPY ["src/Knowz.Core/Knowz.Core.csproj", "src/Knowz.Core/"]
COPY ["src/Knowz.SelfHosted.Infrastructure/Knowz.SelfHosted.Infrastructure.csproj", "src/Knowz.SelfHosted.Infrastructure/"]
COPY ["src/Knowz.SelfHosted.Application/Knowz.SelfHosted.Application.csproj", "src/Knowz.SelfHosted.Application/"]
COPY ["src/Knowz.SelfHosted.API/Knowz.SelfHosted.API.csproj", "src/Knowz.SelfHosted.API/"]

# Restore NuGet packages
RUN dotnet restore "src/Knowz.SelfHosted.API/Knowz.SelfHosted.API.csproj"

# Copy all source code and publish
COPY . .
WORKDIR "/source/src/Knowz.SelfHosted.API"
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Final stage: Minimal runtime image
FROM base AS final
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# Set ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

# Install curl for healthcheck + create file storage directory
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/files /data/keys && chown -R $APP_UID:$APP_UID /data/files /data/keys

# anydoc runtime assets (INFRA_AnydocImageBundling R5/R6). Only the `node` binary is copied, not the
# whole node distribution: the CLI is pure JS plus napi prebuilds that live under @firecrawl.
# /usr/local/bin/anydoc is a HARD CONTRACT - probe step 2 of the SVC_AnydocDocumentConverter locator -
# and must not be renamed. Same post-mortem motivation as the ffmpeg/ONNX blocks: a tool present in three
# images and missing from the fourth degrades silently. MUST stay before any USER directive (the RUN
# needs root, and the copied 0755 paths must remain readable by APP_UID).
COPY --from=anydoc /usr/local/bin/node /usr/local/bin/node
COPY --from=anydoc /usr/local/lib/node_modules/@firecrawl /opt/anydoc/node_modules/@firecrawl
RUN printf '#!/bin/sh\nexec /usr/local/bin/node /opt/anydoc/node_modules/@firecrawl/anydoc/cli.js "$@"\n' \
      > /usr/local/bin/anydoc && chmod +x /usr/local/bin/anydoc

# Switch to built-in non-root user (APP_UID=1654 in .NET 10 base image)
USER $APP_UID

EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Knowz.SelfHosted.API.dll"]
