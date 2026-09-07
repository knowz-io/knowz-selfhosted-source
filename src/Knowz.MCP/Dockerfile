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

# Copy csproj files and restore dependencies (layer caching)
COPY ["src/Knowz.Core/Knowz.Core.csproj", "src/Knowz.Core/"]
COPY ["src/Knowz.MCP/Knowz.MCP.csproj", "src/Knowz.MCP/"]
RUN dotnet restore "src/Knowz.MCP/Knowz.MCP.csproj"

# Copy all source code and publish
COPY . .
WORKDIR /source/src/Knowz.MCP
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

# Install curl for healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Switch to built-in non-root user (APP_UID=1654 in .NET 10 base image)
USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
  CMD curl -f http://localhost:8080/.well-known/oauth-authorization-server || exit 1

ENTRYPOINT ["dotnet", "Knowz.MCP.dll"]
