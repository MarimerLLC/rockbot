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

# Copy solution and project files for layer-cached restore
COPY RockBot.slnx .
COPY src/RockBot.UserProxy.Blazor/RockBot.UserProxy.Blazor.csproj src/RockBot.UserProxy.Blazor/
COPY src/RockBot.UserProxy/RockBot.UserProxy.csproj src/RockBot.UserProxy/
COPY src/RockBot.UserProxy.Abstractions/RockBot.UserProxy.Abstractions.csproj src/RockBot.UserProxy.Abstractions/
COPY src/RockBot.Messaging.RabbitMQ/RockBot.Messaging.RabbitMQ.csproj src/RockBot.Messaging.RabbitMQ/
COPY src/RockBot.Messaging.Abstractions/RockBot.Messaging.Abstractions.csproj src/RockBot.Messaging.Abstractions/

RUN dotnet restore src/RockBot.UserProxy.Blazor/RockBot.UserProxy.Blazor.csproj

COPY src/ src/

WORKDIR /src/src/RockBot.UserProxy.Blazor
RUN dotnet build RockBot.UserProxy.Blazor.csproj -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish RockBot.UserProxy.Blazor.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RockBot.UserProxy.Blazor.dll"]
