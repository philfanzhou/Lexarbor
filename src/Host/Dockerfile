# ========== Stage 1: Build Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://mirrors.huaweicloud.com/repository/npm/
ENV npm_config_registry=${NPM_REGISTRY}
ENV npm_config_replace_registry_host=always
WORKDIR /frontend
COPY src/services/ruoyu.vocabulary/frontend/package*json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY src/services/ruoyu.vocabulary/frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/services/ruoyu.vocabulary/src/Database/Ruoyu.Study.Vocabulary.Database.csproj src/services/ruoyu.vocabulary/src/Database/
COPY src/services/ruoyu.vocabulary/src/Domain/Ruoyu.Study.Vocabulary.Domain.csproj src/services/ruoyu.vocabulary/src/Domain/
COPY src/services/ruoyu.vocabulary/src/Service/Ruoyu.Study.Vocabulary.Service.csproj src/services/ruoyu.vocabulary/src/Service/
COPY src/services/ruoyu.vocabulary/src/Host/Ruoyu.Study.Vocabulary.Host.csproj src/services/ruoyu.vocabulary/src/Host/
COPY src/ruoyu.common/backend/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj src/ruoyu.common/backend/Ruoyu.Study.Common/
COPY src/ruoyu.common/backend/Ruoyu.Study.Consul.Shared/Ruoyu.Study.Consul.Shared.csproj src/ruoyu.common/backend/Ruoyu.Study.Consul.Shared/

RUN dotnet restore "src/services/ruoyu.vocabulary/src/Host/Ruoyu.Study.Vocabulary.Host.csproj"

COPY src/services/ruoyu.vocabulary/src/ src/services/ruoyu.vocabulary/src/
COPY src/ruoyu.common/ src/ruoyu.common/
COPY --from=frontend-build /wwwroot ./src/services/ruoyu.vocabulary/src/Host/wwwroot

RUN dotnet publish "src/services/ruoyu.vocabulary/src/Host/Ruoyu.Study.Vocabulary.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=backend-build /app/publish .
EXPOSE 5008
ENTRYPOINT ["dotnet", "Ruoyu.Study.Vocabulary.Host.dll"]
