# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY RockBot.slnx .
COPY src/RockBot.UserProxy.Blazor/RockBot.UserProxy.Blazor.csproj src/RockBot.UserProxy.Blazor/
COPY src/RockBot.UserProxy/RockBot.UserProxy.csproj src/RockBot.UserProxy/
COPY src/RockBot.UserProxy.Abstractions/RockBot.UserProxy.Abstractions.csproj src/RockBot.UserProxy.Abstractions/
COPY src/RockBot.Messaging.RabbitMQ/RockBot.Messaging.RabbitMQ.csproj src/RockBot.Messaging.RabbitMQ/
COPY src/RockBot.Messaging.Abstractions/RockBot.Messaging.Abstractions.csproj src/RockBot.Messaging.Abstractions/

# Restore dependencies
RUN dotnet restore src/RockBot.UserProxy.Blazor/RockBot.UserProxy.Blazor.csproj

# Copy source code
COPY src/ src/

# Build and publish
WORKDIR /src/src/RockBot.UserProxy.Blazor
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r rockbot && useradd -r -g rockbot rockbot

# Copy published files
COPY --from=build /app/publish .

# Set ownership
RUN chown -R rockbot:rockbot /app

# Switch to non-root user
USER rockbot

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "RockBot.UserProxy.Blazor.dll"]
