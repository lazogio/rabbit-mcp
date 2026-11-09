# Copyright (c) 2026 lazogio
# SPDX-License-Identifier: MIT
# Builds the sample's two containers from source (build context: the repository root):
#   target "service" — the MassTransit shop service
#   target "mcp"     — rabbit-mcp (HTTP) with the shop's contracts DLL
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY samples/Shop/ samples/Shop/
RUN dotnet publish samples/Shop/Shop.Service -c Release -o /out/service \
 && dotnet publish src/RabbitMcp.Server -c Release -o /out/mcp \
 && dotnet build samples/Shop/Shop.Contracts -c Release -o /out/contracts

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30 AS service
WORKDIR /app
COPY --from=build /out/service .
USER $APP_UID
ENTRYPOINT ["dotnet", "Shop.Service.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c AS mcp
WORKDIR /app
COPY --from=build /out/mcp .
COPY --from=build /out/contracts /contracts
ENV RabbitMcp__Transport=http \
    RabbitMcp__HttpUrls=http://0.0.0.0:5080 \
    RabbitMcp__ContractAssemblies=/contracts/Shop.Contracts.dll
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["./rabbit-mcp"]
