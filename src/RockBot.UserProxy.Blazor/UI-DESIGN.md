# RockBot Blazor UI Design

## Layout Overview

The RockBot Blazor chat interface consists of three main sections in a full-height viewport layout:

### 1. Header Bar (Fixed Top)
- **Background**: Primary blue (#0d6efd)
- **Content**: 
  - Left: "RockBot Chat" title (h4 size)
  - Right: Dark/Light mode toggle button (🌙/☀️ emoji)
- **Styling**: White text, padding, subtle shadow

### 2. Messages Area (Scrollable, Flex-Grow)
- **Background**: Light gray (#f5f5f5)
- **Content**: Chat message bubbles
  
#### User Messages (Right-Aligned)
- **Position**: Right side of screen
- **Background**: Primary blue (#0d6efd)
- **Text Color**: White
- **Max Width**: 70% of container
- **Styling**: Rounded corners, padding, subtle shadow
- **Components**:
  - Message text
  - Timestamp (small, opacity 0.75)

#### Agent Messages (Left-Aligned)
- **Position**: Left side of screen
- **Background**: White
- **Text Color**: Dark (#212529)
- **Max Width**: 70% of container
- **Border**: Light gray
- **Styling**: Rounded corners, padding, subtle shadow
- **Components**:
  - Agent name (bold, small, top)
  - Message content (supports HTML from Markdown)
  - Timestamp (small, opacity 0.75)

#### Error Messages
- **Background**: Danger red
- **Text Color**: White
- **Aligned**: Left (like agent messages)

#### Thinking Indicator
- **Background**: Light gray/white
- **Aligned**: Left (like agent messages)
- **Components**:
  - Animated spinner (rotating circle)
  - Text: "Thinking..." or custom progress message

### 3. Input Bar (Fixed Bottom)
- **Background**: Light gray (#f8f9fa)
- **Border**: Top border only
- **Styling**: Padding, subtle shadow (top)
- **Components**:
  - Text input (flex-grow, full width minus button)
    - Placeholder: "Type your message..."
    - Disabled when processing
  - Send button (primary blue)
    - Disabled when message empty or processing

## Responsive Design

### Desktop (> 768px)
- Message bubbles: Max 70% width
- Full padding and spacing
- Header: Full-size h4 title

### Tablet (576px - 768px)
- Message bubbles: Max 85% width
- Standard padding

### Mobile (< 576px)
- Message bubbles: Max 90% width
- Reduced padding on messages area
- Reduced padding on input bar
- Smaller header title (h5 equivalent)

## Dark Mode

When dark mode is activated (via toggle button or system preference):
- **Body Background**: Dark gray (#212529)
- **Messages Area**: Very dark (#1a1a1a)
- **Agent Messages**: Dark gray (#2d2d2d) background, white text
- **Input Bar**: Dark gray (#2d2d2d) background
- **Form Controls**: Dark background (#1a1a1a), white text, dark borders (#444)
- **User Messages**: Unchanged (primary blue, white text)
- **Header**: Unchanged (primary blue, white text)

## Markdown Support

Agent messages support rich text rendering through Markdown:
- **Bold**: `**text**`
- **Italic**: `*text*`
- **Lists**: Bulleted and numbered
- **Code**: Inline `code` and code blocks
- **Links**: Clickable hyperlinks
- **Headings**: H1-H6
- **Tables**: Full table support
- **Blockquotes**: Styled quotes

Code blocks have:
- Light background (#f6f8fa)
- Padding
- Rounded corners
- Horizontal scrolling

## Accessibility Features

- ARIA labels on input and button
- Spinner has `aria-hidden="true"`
- Proper semantic HTML
- Focus outlines on interactive elements
- Disabled state clearly indicated
- Good color contrast ratios
- Keyboard navigation support

## Animation

- **Spinner**: Continuous rotation (1s linear)
- **Auto-scroll**: Smooth scroll to bottom on new messages
- **State Changes**: Instant update via Blazor's SignalR connection

## Technical Implementation

- **Framework**: Blazor Server with InteractiveServer render mode
- **Real-time**: Blazor's built-in SignalR (automatic)
- **State Management**: ChatStateService with event notifications
- **Markdown**: Markdig library with advanced extensions
- **CSS Framework**: Bootstrap 5
- **Custom Styling**: Additional CSS in app.css for chat-specific styling
