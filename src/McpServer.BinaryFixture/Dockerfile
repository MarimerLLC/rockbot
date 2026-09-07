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

# Copy project file and build props for layer-cached restore
COPY Directory.Build.props .
COPY src/McpServer.BinaryFixture/McpServer.BinaryFixture.csproj src/McpServer.BinaryFixture/

RUN dotnet restore src/McpServer.BinaryFixture/McpServer.BinaryFixture.csproj

COPY src/McpServer.BinaryFixture/ src/McpServer.BinaryFixture/

WORKDIR /src/src/McpServer.BinaryFixture
RUN dotnet build McpServer.BinaryFixture.csproj -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish McpServer.BinaryFixture.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServer.BinaryFixture.dll"]
