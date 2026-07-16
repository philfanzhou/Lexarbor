FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5008
ENV ASPNETCORE_URLS=http://+:5008

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/services/ruoyu.vocabulary/src/Database/Ruoyu.Study.Vocabulary.Database.csproj src/services/ruoyu.vocabulary/src/Database/
COPY src/services/ruoyu.vocabulary/src/Domain/Ruoyu.Study.Vocabulary.Domain.csproj src/services/ruoyu.vocabulary/src/Domain/
COPY src/services/ruoyu.vocabulary/src/Service/Ruoyu.Study.Vocabulary.Service.csproj src/services/ruoyu.vocabulary/src/Service/
COPY src/services/ruoyu.vocabulary/src/Host/Ruoyu.Study.Vocabulary.Host.csproj src/services/ruoyu.vocabulary/src/Host/
COPY src/ruoyu.common/backend/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj src/ruoyu.common/backend/Ruoyu.Study.Common/

RUN dotnet restore "src/services/ruoyu.vocabulary/src/Host/Ruoyu.Study.Vocabulary.Host.csproj"

COPY src/services/ruoyu.vocabulary/src/ src/services/ruoyu.vocabulary/src/
COPY src/ruoyu.common/ src/ruoyu.common/

RUN dotnet publish "src/services/ruoyu.vocabulary/src/Host/Ruoyu.Study.Vocabulary.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Ruoyu.Study.Vocabulary.Host.dll"]
