FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Runs on the build host; -a $TARGETARCH restores and publishes for the target platform
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Clear Windows-specific NuGet fallback folders that break Linux builds
RUN printf '<configuration><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>' \
    > /src/NuGet.config

# Copy project file and build props for layer-cached restore
COPY Directory.Build.props .
COPY src/McpServer.ElicitationFixture/McpServer.ElicitationFixture.csproj src/McpServer.ElicitationFixture/

RUN dotnet restore src/McpServer.ElicitationFixture/McpServer.ElicitationFixture.csproj -a $TARGETARCH

COPY src/McpServer.ElicitationFixture/ src/McpServer.ElicitationFixture/

WORKDIR /src/src/McpServer.ElicitationFixture
RUN dotnet build McpServer.ElicitationFixture.csproj -c $BUILD_CONFIGURATION -o /app/build --no-restore -a $TARGETARCH

FROM build AS publish
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish McpServer.ElicitationFixture.csproj -c $BUILD_CONFIGURATION -a $TARGETARCH -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServer.ElicitationFixture.dll"]
