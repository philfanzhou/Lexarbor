# Both build stages are pinned to the machine running the build rather than to
# the image being produced. The release workflow builds linux/amd64 and
# linux/arm64, and without this pin the ARM64 leg re-runs the whole Node and
# .NET build under QEMU emulation. Nothing either stage produces is
# architecture-specific: the frontend emits static assets, and the backend
# publishes framework-dependent IL with UseAppHost=false and no
# RuntimeIdentifier, so native dependencies such as SQLitePCLRaw ship as
# runtimes/<rid>/native/ and are selected when the application loads. Only the
# runtime stage below is pulled per target architecture.

# ========== Stage 1: Build Frontend ==========
FROM --platform=$BUILDPLATFORM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://registry.npmjs.org/
ENV npm_config_registry=${NPM_REGISTRY}
WORKDIR /frontend
COPY frontend/package*json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props Lexarbor.sln ./
COPY src/Lexarbor.Database/Lexarbor.Database.csproj src/Lexarbor.Database/
COPY src/Lexarbor.Domain/Lexarbor.Domain.csproj src/Lexarbor.Domain/
COPY src/Lexarbor.Service/Lexarbor.Service.csproj src/Lexarbor.Service/
COPY src/Lexarbor.Host/Lexarbor.Host.csproj src/Lexarbor.Host/

RUN dotnet restore "src/Lexarbor.Host/Lexarbor.Host.csproj"

COPY src/ src/
COPY --from=frontend-build /frontend/dist ./src/Lexarbor.Host/wwwroot

RUN dotnet publish "src/Lexarbor.Host/Lexarbor.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=backend-build /app/publish .
RUN mkdir -p /app/data
VOLUME ["/app/data"]
EXPOSE 5008
ENTRYPOINT ["dotnet", "Lexarbor.Host.dll"]
