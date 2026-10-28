# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
# Builds the sample's two containers from source (build context: the repository root):
#   target "service" — the MassTransit shop service
#   target "mcp"     — rabbit-mcp (HTTP) with the shop's contracts DLL
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:afd0ccff9d306812a69ef0f47458376b6acb544ae866cfc4b8d28ff0eef35ea7 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY samples/Shop/ samples/Shop/
RUN dotnet publish samples/Shop/Shop.Service -c Release -o /out/service \
 && dotnet publish src/RabbitMcp.Server -c Release -o /out/mcp \
 && dotnet build samples/Shop/Shop.Contracts -c Release -o /out/contracts

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:939555726cae4c916d212323ac3c0792996dcac2614f00e82cc40fcf059e1af3 AS service
WORKDIR /app
COPY --from=build /out/service .
USER $APP_UID
ENTRYPOINT ["dotnet", "Shop.Service.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS mcp
WORKDIR /app
COPY --from=build /out/mcp .
COPY --from=build /out/contracts /contracts
ENV RabbitMcp__Transport=http \
    RabbitMcp__HttpUrls=http://0.0.0.0:5080 \
    RabbitMcp__ContractAssemblies=/contracts/Shop.Contracts.dll
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["./rabbit-mcp"]
