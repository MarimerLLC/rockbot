# RockBot.UserProxy.Blazor

A Blazor Server web application that provides a chat interface for interacting with RockBot agents.

## Features

- **Real-time Chat UI**: Interactive chat interface built with Blazor Server
- **Markdown Support**: Agent responses are rendered as rich HTML from Markdown
- **Responsive Design**: Works seamlessly on desktop, tablet, and mobile devices
- **Dark/Light Mode**: Toggle between light and dark themes
- **Progress Indicators**: Visual feedback when agents are processing requests
- **Bootstrap Styling**: Clean, modern UI using Bootstrap 5

## Running the Application

### Prerequisites

- .NET 10 SDK
- RabbitMQ server (for message bus connectivity)

### Development

```bash
cd src/RockBot.UserProxy.Blazor
dotnet run
```

The app will be available at `https://localhost:5001` (or `http://localhost:5000`).

### Configuration

The application uses standard .NET configuration. You can configure RabbitMQ connection via:

- `appsettings.json`
- Environment variables
- User secrets (recommended for local development)

Example environment variables:

```bash
export ROCKBOT_RABBITMQ_HOST=localhost
export ROCKBOT_RABBITMQ_PORT=5672
export ROCKBOT_RABBITMQ_USERNAME=guest
export ROCKBOT_RABBITMQ_PASSWORD=guest
```

## Docker Deployment

Build the Docker image:

```bash
docker build -t rockbot-blazor -f src/RockBot.UserProxy.Blazor/Dockerfile .
```

Run the container:

```bash
docker run -p 8080:8080 \
  -e ROCKBOT_RABBITMQ_HOST=rabbitmq \
  rockbot-blazor
```

## Architecture

The application follows the same messaging pattern as the CLI user proxy:

1. User sends a message via the web UI
2. `UserProxyService` publishes the message to the RabbitMQ message bus
3. Agent(s) process the message and send replies
4. `BlazorUserFrontend` receives replies and updates the UI via `ChatStateService`
5. Blazor's built-in SignalR connection automatically pushes updates to the browser

## Project Structure

- `Components/` - Root Blazor components (App, Routes)
- `Pages/` - Routable pages (Chat.razor)
- `Services/` - Business logic services
  - `ChatStateService` - Manages chat state and notifies UI of changes
  - `BlazorUserFrontend` - Implements `IUserFrontend` for Blazor
- `wwwroot/` - Static assets (CSS, images)
