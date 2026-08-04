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
# Demo bundles (docs/08 §M7): the Gateway seeds these at startup so a fresh deploy
# lands on a populated app. Harmless for worker images.
COPY seeds/ ./seeds/
# git: required by Workers.Analysis for S0 acquire (shallow clones); small cost to
# carry in the shared image.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    # uid/gid 1000 matches the deployment's bind-mount ownership convention
    # (puid/pgid 1000); a system uid here broke workspace writes in production.
    # The Ubuntu-based aspnet image already has uid/gid 1000 ("ubuntu") — adopt
    # and rename it rather than colliding with it.
    && (getent group 1000 >/dev/null && groupmod -n codeexploder "$(getent group 1000 | cut -d: -f1)" \
        || groupadd -g 1000 codeexploder) \
    && (getent passwd 1000 >/dev/null && usermod -l codeexploder -d /var/lib/code-exploder "$(getent passwd 1000 | cut -d: -f1)" \
        || useradd -u 1000 -g codeexploder -d /var/lib/code-exploder codeexploder) \
    && mkdir -p /var/lib/code-exploder/workspaces \
    && chown -R 1000:1000 /var/lib/code-exploder
USER codeexploder
ENTRYPOINT ["sh", "-c", "exec dotnet ${PROJECT_DLL}"]
