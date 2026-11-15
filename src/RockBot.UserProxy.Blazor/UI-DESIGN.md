# RockBot Blazor UI Design

The chat UI follows the **RockBot Design System** (a Claude design-system artifact:
https://claude.ai/artifact/By42C3bKBZFMSpH1ushPZ5). Its `ui_kits/blazor-chat` page is the
reference mock; `components/chat.css` is the reference CSS.

- `wwwroot/css/tokens.css` is a verbatim copy of the design system's generated `tokens.css`
  (`--rb-*` colours, radii, shadows, type scale). Re-sync it by copying the file again.
- `wwwroot/css/app.css` is written entirely in terms of those tokens. App-level overrides (fonts,
  a few darker text colours for contrast) sit in its first `:root` block.
- Bootstrap 5 stays as the base layer (forms, buttons in forms, utilities); colour comes from the
  tokens, not Bootstrap's colour utilities.

## Rules

- **Blue does interaction, orange is RockBot.** Blue: header, user bubbles, Send, focus rings,
  links, MCP questions. Orange: the agent avatar and the left edge of RockBot's replies, interim
  lines, the thinking indicator, subagent activity, the sign-in card's top edge.
- **Dashed borders mean interim, activity or collapsible.** Never decoration.
- **Category tints carry meaning**: subagent orange, inbound A2A blue, background task amber,
  saved reference indigo. Don't recolour them.
- **No glow in product chrome.** The one exception is a live header activity slot.
- **No emoji.** The 🌙/☀️ theme toggle is the only exception. Icons are monochrome stroke SVG in
  `currentColor`, from `Components/RbIcon.razor`; icon-only buttons carry an `aria-label`.
- **Contrast**: text 4.5:1 on its ground in both themes; borders, focus rings and icons 3:1.
- **Fonts**: the system font stack, and a system monospace stack for code. No webfonts.

## Layout

Three rows filling the viewport: header (fixed), messages (scrolls), composer (fixed). The plan
panel, when the agent has a task list, sits between messages and composer. Nothing floats over
the chat area except the saved-responses modal and the version popover.

### Header (`.chat-header`)

- **Left**: the app mark (`wwwroot/images/rockbot-mark-*.png`, 30px, 7px radius; the real
  artwork, never redrawn) and "<agent name> Chat".
- **Activity row** (`Components/ActivityIcon.razor`): one circular slot per actor, always in this
  order — RockBot, subagents, inbound A2A, background tasks. Idle slots dim; live ones get their
  ring colour, a pulse and, for more than one instance, a count badge. Tooltips list what is
  running. The RockBot slot is the busy indicator.
- **Connection pill** (`Components/ConnectionPill.razor`): dot plus the word Connected or
  Disconnected (never colour alone). Clicking opens the version popover (bus state, UI version,
  agent version), using the browser's own `popover`, so Escape and clicking away close it.
- **Buttons**: saved responses (bookmark), theme toggle, Sign out (when signed in).

### Messages

Every agent-side bubble has a 30px circular mark on its left (`Components/CategoryAvatar.razor`),
so each actor reads as one shape; interim lines and errors keep the column empty.

| Kind | Look |
| --- | --- |
| User message | Right-aligned, `--rb-primary-600` fill, white text |
| RockBot reply | "RB" avatar (orange ring on night navy); white bubble, 3px orange left edge, `4px 18px 18px 18px` radius; feedback bar |
| Interim line / thinking | Dashed orange border, orange tint, italic; the thinking indicator adds a THINKING badge and an orange spinner |
| Subagent / inbound A2A / background task | Category avatar; dashed, tinted bubble; ALL-CAPS badge, agent name, italic preview; collapsed to one line, chevron expands |
| Activity log | The same dashed one-liner, with a live spinner while it is still receiving entries; expands to the entries and tool-call cards |
| Saved reference | Bookmark avatar; indigo border and badge; dismiss button |
| MCP question | Blue border and badge; the form built from the server's schema |
| Error | `--rb-danger` fill, white text |

Feedback bar: thumbs up / thumbs down (blue ring when chosen) and, on final replies, the indigo
bookmark that saves the response.

Markdown (via Markdig, sanitized) supports bold, italic, lists, links, headings, tables,
blockquotes and code. Inline code takes the text colour on a faint tint; code blocks use
`--rb-bg-code`.

### Composer (`.composer`)

Attach (paperclip), an auto-growing textarea (44–160px, `field-sizing: content`), Cancel while
the agent is working, and Send. The textarea's focus shows a 2px `--rb-primary` outline with a
halo. Below 480px of composer width (a container query) Send and Cancel become icon-only. Staged
attachments show as removable chips above the row.

## Responsive

| Width | Bubbles | Other |
| --- | --- | --- |
| > 768px | 70% | |
| 576–768px | 85% | Smaller title |
| < 576px | 90% | Avatars hidden; tighter padding; header shows the mark, only the live activity slots, the connection word and icon buttons |

## Dark mode

`body.dark-mode`, set from the system preference on load and by the toggle. The header drops to
a muted navy (`#10243f`); surfaces use the `--rb-*-dark*` tokens; RockBot's orange edge thickens
to 4px with a soft glow so it still reads; category bubbles share a dark panel with darker tints
on their badges.

## Sign-in pages

`/login` and `/access-denied` are static SSR. A centred card with the app mark, an orange top
edge, and provider buttons that turn blue on hover.

## Accessibility

- Icon-only buttons have `aria-label`s; decorative SVGs are `aria-hidden`.
- Activity slots carry visually hidden text ("Subagents: 2 active").
- Focus: 2px `--rb-primary` outline with a 2px offset on every control (white on the header).
- Animations stop under `prefers-reduced-motion`.

## Technical implementation

- **Framework**: Blazor Server, InteractiveServer render mode on the chat page only
- **State**: `ChatStateService` (RockBot.UserProxy.ChatState), applied as events
- **Markdown**: Markdig through `SafeMarkdownRenderer`
- **CSS**: Bootstrap 5 (CDN) + `tokens.css` + `app.css`
