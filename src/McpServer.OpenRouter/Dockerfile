FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Clear Windows-specific NuGet fallback folders that break Linux builds
RUN printf '<configuration><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>' \
    > /src/NuGet.config

# Copy project file for layer-cached restore
COPY src/McpServer.OpenRouter/McpServer.OpenRouter.csproj src/McpServer.OpenRouter/

RUN dotnet restore src/McpServer.OpenRouter/McpServer.OpenRouter.csproj

COPY src/McpServer.OpenRouter/ src/McpServer.OpenRouter/

WORKDIR /src/src/McpServer.OpenRouter
RUN dotnet build McpServer.OpenRouter.csproj -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish McpServer.OpenRouter.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServer.OpenRouter.dll"]
