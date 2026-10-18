# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
# rabbit-mcp as a shared HTTP server (RabbitMcp:Transport=http). Multi-arch (amd64, arm64): the SDK stage runs on
# the build machine and cross-publishes, so no emulation is needed.
#   docker run -p 5080:5080 -e RabbitMcp__Uri=amqp://user:pass@rabbit:5672/ -e RabbitMcp__ApiKeys="team:KEY" ghcr.io/lazogio/rabbit-mcp
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-local
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/RabbitMcp.Contracts/RabbitMcp.Contracts.csproj src/RabbitMcp.Contracts/
COPY src/RabbitMcp.Samples/RabbitMcp.Samples.csproj src/RabbitMcp.Samples/
COPY src/RabbitMcp.Server/RabbitMcp.Server.csproj src/RabbitMcp.Server/
RUN case "$TARGETARCH" in amd64) echo linux-x64 ;; arm64) echo linux-arm64 ;; *) echo "unsupported $TARGETARCH" >&2; exit 1 ;; esac > /rid \
 && dotnet restore src/RabbitMcp.Server -r "$(cat /rid)"

COPY src/ src/
RUN dotnet publish src/RabbitMcp.Server -c Release -r "$(cat /rid)" --self-contained false --no-restore \
      -p:Version="$VERSION" -p:DebugType=none -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY LICENSE /app/LICENSE

# Listen on all interfaces inside the container; authentication is still required (ApiKeys / JwtAuthority).
ENV RabbitMcp__Transport=http \
    RabbitMcp__HttpUrls=http://0.0.0.0:5080
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["./rabbit-mcp"]
