# RockBot Blazor UI Design

The chat UI follows the **RockBot Design System** (a Claude design-system artifact:
https://claude.ai/artifact/By42C3bKBZFMSpH1ushPZ5). Its `ui_kits/blazor-chat` page is the
reference mock; `components/chat.css` is the reference CSS.

- `wwwroot/css/tokens.css` is a verbatim copy of the design system's generated `tokens.css`
  (`--rb-*` colours, radii, shadows, type scale). Re-sync it by copying the file again.
- `wwwroot/css/app.css` is written entirely in terms of those tokens. Its first `:root` block
  holds the app-level overrides: system fonts, and four dark-mode interaction shades
  (`--app-*`) the design system doesn't define yet.
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
panel, when the agent has a task list, sits between messages and composer. The activity panel,
when open, sits between the header and the messages. Nothing floats over the chat area except
the saved-responses modal, the agent info dialog and the version popover.

## Simple and Detailed views (#828)

One transcript, two views, switched by the header's detail toggle and remembered per browser in
`localStorage` (Simple when storage is unavailable). The view is a filter on rendering in the chat
page, not chat state: `ChatStateService` is a singleton shared by every browser.

| Simple (the default) shows | Only Detailed shows |
| --- | --- |
| User messages, RockBot's final replies, the user's scheduled-task results | Subagent / worker, inbound A2A and system background-task bubbles |
| Errors, saved references, MCP questions, the MCP sign-in banner | Activity logs, including the primary progress log and its tool-call cards |
| One Thinking line while busy, with the agent's live status text | Interim lines |
| The primary agent's plan | Subagent plans |

In Simple view the Thinking line shows whenever the agent is busy. A subagent result that no
primary reply follows before the next user turn shows as a compact bubble. Auto-scroll counts
only the messages the view draws.

## Activity panel

Clicking a header activity slot opens the activity panel on that slot's tab. It is the RockBot
Design System's `ActivityPanel`, and it works the same in both views. Clicking the same slot
again, the close button or Escape closes it, and focus returns to the slot.

- **Placement**: in the page flow directly under the header (with the MCP sign-in banner). It
  pushes the messages down, is capped at `min(40vh, 28rem)` (50vh below 576px) and scrolls
  inside itself.
- **Tabs** (`tablist`, arrow keys move between them): RockBot, Subagents, Inbound A2A,
  Background tasks; below 576px the short labels RockBot, Subagents, A2A, Tasks.
  - The selected tab is bold with a `--rb-primary` underline.
  - Each tab has a dot in its kind's ring colour, which pulses while that kind has running work.
  - A running count follows the label.
- **Running**: a card per item, with:
  - the category badge (RockBot's is orange on `--rb-night`, like its avatar), the name, a
    spinner and elapsed time;
  - the status line, and plan progress ("Step 2 of 4 · …");
  - its activity log, expandable, using the same entries and tool-call cards as the transcript.
- **Recently finished**: the last few results this session, each expandable to its log and result.
- **Cards**: the category tint, a solid `--rb-border` hairline, `0.375rem` radius, hairline
  shadow. Dashed borders stay the transcript's signal; no glow.

## Agent info dialog (#834)

What the agent reports about itself, read over the bus without its LLM
(`Components/AgentInfoDialog.razor`). Opened by the header's info button and by the link at the
foot of the version popover; closed by its close button, Escape or a click on the overlay, after
which focus returns to the info button.

- **Modal**, like the saved-responses dialog: everything behind it is inert. Up to 720px wide and
  80vh tall; below 576px it is a full-width sheet from the bottom (90vh).
- **Tabs** (`tablist`, arrow keys): Agent, Models, Pricing, Usage, in the activity panel's tab
  style; the row scrolls sideways when it doesn't fit.
- **Agent**: a definition list (versions, bus state, uptime, when the models were loaded).
- **Models**: one section per tier. A fallback chain is a numbered list. Each model is a card
  with its id in monospace, its facts as small pills (service, host, reasoning effort, image
  support), and its price. A model no pricing row matches shows its price line in
  `--rb-danger-700` (`--rb-danger-fg-dark` in dark mode): it is costed at $0.
- **Pricing**: a table with numeric columns right-aligned in tabular figures, a sticky header,
  scrolling sideways on narrow screens. A cache rate the row leaves out shows its default
  (input, 1.25×, 2×).
- **Usage**: a row of cost cards (Today, Last 7 days, Last 30 days, This chat), each the cost large
  with requests, tokens and cache share beneath. Then tables: by day (with a thin `--rb-primary`
  bar for the day's share of the busiest day, hidden below 576px), by model, by workload.
  Unpriced requests get the bordered note.
- **Unavailable** (an older agent, or no bus): a bordered note in the panel, never an error colour.

### Header (`.chat-header`)

- **Left**: the app mark (`wwwroot/images/rockbot-mark-*.png`, 30px, 7px radius; the real
  artwork, never redrawn) and "<agent name> Chat".
- **Activity row** (`Components/ActivityIcon.razor`): one circular slot per actor, always in this
  order — RockBot, subagents, inbound A2A, background tasks. An idle slot is a plain translucent
  circle with an 85% white glyph (3.3:1 on the blue header, 9.4:1 on the dark one: it is a button,
  so never fade it below 3:1); live ones get their ring colour on a night disc, a pulse and, for
  more than one instance, a count badge on the slot's corner, clear of the glyph. Tooltips list
  what is running. The RockBot slot is the busy indicator. Each slot is a button (`aria-expanded`,
  `aria-controls`) that opens the activity panel on its tab, idle or not; idle slots brighten on
  hover and focus, and the open slot has a 2px white tick under it.
- **Detail toggle**: Lucide `list-tree` icon button, `aria-pressed` when Detailed, after the
  saved-responses button.
- **Connection pill** (`Components/ConnectionPill.razor`): dot plus the word Connected or
  Disconnected (never colour alone). Clicking opens the version popover (bus state, UI version,
  agent version), using the browser's own `popover`, so Escape and clicking away close it. Its
  last row is a link that opens the agent info dialog.
- **Buttons**: agent info (Lucide `info`), saved responses (bookmark), detail toggle, theme
  toggle, Sign out (when signed in).

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
| < 576px | 90% | Avatars hidden; tighter padding; header shows the mark, the RockBot slot plus the live activity slots (the RockBot slot stays, so the activity panel is always reachable), the connection word and icon buttons |

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
- Activity slots carry their state in words ("Subagents: 2 active"); as buttons they also say
  they show details. Tool-call cards in the activity panel give finished / failed / running in
  words beside the icon.
- Focus: 2px `--rb-primary` outline with a 2px offset on every control (white on the header).
- Animations stop under `prefers-reduced-motion`.

## Technical implementation

- **Framework**: Blazor Server, InteractiveServer render mode on the chat page only
- **State**: `ChatStateService` (RockBot.UserProxy.ChatState), applied as events
- **Markdown**: Markdig through `SafeMarkdownRenderer`
- **CSS**: Bootstrap 5 (CDN) + `tokens.css` + `app.css`
