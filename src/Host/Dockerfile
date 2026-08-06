# ========== Stage 1: Build Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://registry.npmjs.org/
ENV npm_config_registry=${NPM_REGISTRY}
WORKDIR /frontend
COPY frontend/package*json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/Database/Lexarbor.Database.csproj src/Database/
COPY src/Domain/Lexarbor.Domain.csproj src/Domain/
COPY src/Service/Lexarbor.Service.csproj src/Service/
COPY src/Host/Lexarbor.Host.csproj src/Host/

RUN dotnet restore "src/Host/Lexarbor.Host.csproj"

COPY src/ src/
COPY --from=frontend-build /wwwroot ./src/Host/wwwroot

RUN dotnet publish "src/Host/Lexarbor.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=backend-build /app/publish .
RUN mkdir -p /app/data
VOLUME ["/app/data"]
EXPOSE 5008
ENTRYPOINT ["dotnet", "Lexarbor.Host.dll"]
