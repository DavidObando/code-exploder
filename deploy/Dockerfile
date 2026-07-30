# Shared multi-stage build for all Code Exploder .NET services.
# Build from the repo root, selecting the service with PROJECT:
#   docker build -f deploy/Dockerfile --build-arg PROJECT=CodeExploder.Gateway -t code-exploder/gateway .
ARG PROJECT=CodeExploder.Gateway

# Web UI build: static assets land in webui/dist and are picked up by the Gateway's
# publish step (IncludeWebUi target). Only the Gateway's wwwroot serves them; worker
# images carry nothing extra. The stage is built once and layer-cached across services.
FROM node:22-alpine AS webui-build
WORKDIR /source/webui
COPY webui/package.json webui/package-lock.json ./
RUN npm ci
COPY webui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /source
COPY global.json Directory.Build.props version.json .editorconfig ./
COPY .git/ .git/
COPY src/ src/
COPY --from=webui-build /source/webui/dist webui/dist
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
ENV PROJECT_DLL=${PROJECT}.dll \
    DOTNET_EnableDiagnostics=0
WORKDIR /app
COPY --from=build /app .
RUN groupadd -r codeexploder && useradd -r -g codeexploder codeexploder \
    && mkdir -p /var/lib/code-exploder/workspaces \
    && chown -R codeexploder:codeexploder /var/lib/code-exploder
USER codeexploder
ENTRYPOINT ["sh", "-c", "exec dotnet ${PROJECT_DLL}"]
