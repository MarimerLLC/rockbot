# RockBot.UserProxy.Blazor - Project Structure

```
RockBot.UserProxy.Blazor/
├── Components/
│   ├── App.razor                    # Root HTML layout with head/body
│   ├── Routes.razor                 # Blazor router configuration
│   └── _Imports.razor               # Global using directives for components
│
├── Pages/
│   ├── Chat.razor                   # Main chat page (route: "/")
│   │   - Message display area
│   │   - Thinking indicator
│   │   - Message input form
│   │   - Dark mode toggle
│   │   - Auto-scroll logic
│   └── _Imports.razor               # Page-specific imports
│
├── Services/
│   ├── BlazorUserFrontend.cs        # IUserFrontend implementation
│   │   - Bridges RockBot messaging to UI
│   │   - Updates ChatStateService
│   │
│   └── ChatStateService.cs          # State management
│       - Tracks messages, processing state
│       - Event-driven UI notifications
│       - Thread-safe message collection
│
├── wwwroot/
│   └── css/
│       └── app.css                  # Custom CSS styles
│           - Chat layout
│           - Message bubbles
│           - Dark mode styles
│           - Responsive design
│           - Animations
│
├── Program.cs                       # Application entry point
│   - Configures Blazor services
│   - Registers RockBot services
│   - Sets up DI container
│
├── RockBot.UserProxy.Blazor.csproj # Project file
│   - .NET 10 web SDK
│   - Package references (Markdig)
│   - Project references (UserProxy, RabbitMQ)
│
├── appsettings.json                 # Application configuration
├── appsettings.Development.json     # Development overrides
│
├── Dockerfile                       # Multi-stage container build
│   - Build stage: .NET 10 SDK
│   - Runtime stage: .NET 10 ASP.NET
│   - Non-root user
│   - Port 8080
│
├── README.md                        # Usage and deployment guide
├── UI-DESIGN.md                     # Detailed UI/UX specification
└── IMPLEMENTATION-SUMMARY.md        # This implementation overview
```

## Key Architecture Decisions

### 1. State Management
- **Pattern**: Singleton service with event notifications
- **Benefits**: Simple, thread-safe, integrates with Blazor's rendering
- **Alternative**: Could use Fluxor or other state library (not needed for this scope)

### 2. Real-time Updates
- **Pattern**: Blazor Server's built-in SignalR
- **Benefits**: Automatic, no custom code needed, handles reconnection
- **Alternative**: Could use custom WebSocket (unnecessary complexity)

### 3. Markdown Rendering
- **Library**: Markdig with advanced extensions
- **Benefits**: Fast, comprehensive, extensible
- **Fallback**: HTML encoding with line breaks if parsing fails

### 4. Dark Mode
- **Pattern**: CSS class toggle via JavaScript interop
- **Benefits**: Respects system preference, instant toggle, no page reload
- **Implementation**: CSS variables for theming

### 5. Responsive Design
- **Framework**: Bootstrap 5
- **Benefits**: Battle-tested, accessible, mobile-first
- **Custom**: Additional CSS for chat-specific styling

## Comparison with Alternatives

| Approach | Chosen | Alternative | Reason |
|----------|--------|-------------|--------|
| **UI Framework** | Blazor Server | Blazor WebAssembly | Server mode simpler for RabbitMQ integration |
| **State** | Singleton + Events | SignalR Hub | Blazor Server already uses SignalR |
| **Markdown** | Markdig | Custom parser | Markdig is industry standard |
| **Styling** | Bootstrap 5 | Tailwind/MudBlazor | Bootstrap meets requirements, well-known |
| **Dark Mode** | CSS classes | JS library | Native approach, lightweight |

## Dependencies

### Direct
- **Markdig**: Markdown to HTML conversion
- **Bootstrap 5**: CSS framework (CDN)

### Transitive (via project references)
- RockBot.UserProxy
- RockBot.UserProxy.Abstractions
- RockBot.Messaging.RabbitMQ
- RockBot.Messaging.Abstractions
- Microsoft.AspNetCore.Components.Web
- RabbitMQ.Client

### Development
- .NET 10 SDK
- Docker (for containerization)

## Configuration Sources

Configuration is loaded in this order (later sources override earlier):
1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. User secrets (Development only)
4. Environment variables
5. Command-line arguments

### RabbitMQ Configuration
```bash
# Environment variables
ROCKBOT_RABBITMQ_HOST=localhost
ROCKBOT_RABBITMQ_PORT=5672
ROCKBOT_RABBITMQ_USERNAME=guest
ROCKBOT_RABBITMQ_PASSWORD=guest
```

## Deployment Scenarios

### 1. Development (Local)
```bash
cd src/RockBot.UserProxy.Blazor
dotnet run
```
Access: https://localhost:5001

### 2. Docker (Local)
```bash
docker build -t rockbot-blazor -f src/RockBot.UserProxy.Blazor/Dockerfile .
docker run -p 8080:8080 \
  -e ROCKBOT_RABBITMQ_HOST=host.docker.internal \
  rockbot-blazor
```
Access: http://localhost:8080

### 3. Kubernetes (Production)
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: rockbot-blazor
spec:
  replicas: 2
  template:
    spec:
      containers:
      - name: blazor
        image: rockbot-blazor:latest
        ports:
        - containerPort: 8080
        env:
        - name: ROCKBOT_RABBITMQ_HOST
          value: rabbitmq-service
```

## Performance Considerations

### Scalability
- Each Blazor Server connection uses a SignalR circuit
- Memory: ~250KB per circuit
- Recommended: 2GB RAM = ~8,000 concurrent users
- For higher scale: Use Blazor WebAssembly or add more replicas

### Latency
- RabbitMQ roundtrip: ~10-50ms (local)
- Blazor UI update: ~5-10ms
- Markdown rendering: <1ms for typical messages
- Total user-to-agent: Depends on agent processing time

### Optimization
- Static assets served via CDN (Bootstrap)
- Markdown pipeline is shared/cached
- ChatStateService is singleton (shared across circuits)
- Auto-scroll uses requestAnimationFrame (not implemented, could add)

## Future Enhancements

### Not in Current Scope
1. **Authentication**: Add ASP.NET Core Identity
2. **Multi-user**: Separate session per user
3. **Persistence**: Store chat history in database
4. **File Upload**: Support image/document uploads
5. **Streaming**: Display agent responses as they type
6. **Presence**: Show when agents are typing
7. **Search**: Full-text search of chat history
8. **Export**: Download conversations as PDF/JSON
9. **Admin Panel**: Configure agents, view metrics
10. **PWA**: Progressive Web App for offline support

### Easy Additions
1. **More themes**: Add additional color schemes
2. **Custom avatars**: Agent profile pictures
3. **Emoji picker**: Rich input support
4. **Message editing**: Edit/delete sent messages
5. **Copy buttons**: Copy agent responses
6. **Keyboard shortcuts**: Ctrl+Enter to send, etc.

## Maintenance

### Updating Dependencies
```bash
# Check for updates
dotnet list package --outdated

# Update specific package
dotnet add package Markdig --version <new-version>

# Update all (with caution)
dotnet outdated --upgrade
```

### Testing
```bash
# Run if tests are added
dotnet test

# Build for production
dotnet publish -c Release

# Test Docker build
docker build -t test .
docker run -p 8080:8080 test
```

### Monitoring
- ASP.NET Core metrics (requests, errors)
- Blazor circuit metrics (connections, disconnects)
- RabbitMQ metrics (message throughput)
- Application Insights (if configured)

## Troubleshooting

### Build Errors
- PageTitle warning: Benign, can be ignored
- Render mode errors: Check _Imports.razor has correct using statements

### Runtime Errors
- RabbitMQ connection: Verify ROCKBOT_RABBITMQ_HOST is correct
- SignalR disconnect: Check firewall, WebSocket support
- Dark mode not working: Verify JavaScript is enabled

### Performance Issues
- Too many messages: Add pagination or virtual scrolling
- Slow rendering: Check browser DevTools for bottlenecks
- Memory leaks: Ensure components are disposing properly
