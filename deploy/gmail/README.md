# Gmail MCP server (docker compose)

Runs `gmail-mcp` (`src/McpServer.Gmail`), which gives RockBot one or more Gmail accounts
(personal `@gmail.com` and Google Workspace alike). It can read, search and **draft**, but never send:

| Read | Write |
|---|---|
| `list_accounts`, `search_messages` (Gmail search syntax), `list_messages`, `get_message`, `get_thread`, `list_labels`, `get_attachment` | `create_draft` (new, reply or reply-all, threaded correctly), `update_draft` |

There are no send, delete, trash or label tools. Drafts wait in Gmail's **Drafts** folder for you
to review and send.

> **Drafts-only is enforced by this server, not by Google.** Gmail has no permission for
> "drafts but not send": `gmail.compose`, which drafts need, also allows sending. The token in
> `.env` could therefore send mail if someone used it directly. Treat it like a password.

`get_attachment` returns text files as text. Other files (PDF, Office documents, images) go to
`/rockbot/shared/attachments/` through the agent bridge's capture rule, which `setup.sh` adds, so
RockBot can open them with `analyze_file`.

## 1. Create the Google OAuth client (once, for all accounts)

Use a Google account you control, such as your personal one. The client works for any account
that consents to it.

1. Open <https://console.cloud.google.com/> and create a project, e.g. **RockBot**.
2. **APIs & Services > Library**: find **Gmail API** and click **Enable**.
3. **Google Auth Platform**, also listed as **OAuth consent screen**:
   - **Branding**: app name *RockBot*, your email as the support and developer contact.
   - **Audience**: choose **External**, then **Publish app** so the status is **In production**.
     Don't leave it in *Testing*: Google expires Testing refresh tokens after 7 days, and RockBot
     would lose access every week. You don't need to submit it for verification; for your own
     use, Google just shows an "unverified app" warning when you consent.
   - **Data access**: optional. Add `.../auth/gmail.readonly` and `.../auth/gmail.compose`.
4. **Clients > Create client**: type **Desktop app**, name *RockBot*. Copy the **Client ID**
   and **Client secret**.

## 2. Connect each account

Run it in a terminal on the machine with your browser:

```bash
deploy/gmail/setup.sh --account personal --email you@gmail.com
deploy/gmail/setup.sh --account work --email you@client.example
```

The first run asks for the client ID and secret. Each run then:
- opens Google's consent page; if the browser doesn't open, the link is printed;
- warns "Google hasn't verified this app". Click **Advanced > Go to RockBot (unsafe)**: it's your
  own client. Tick **both** Gmail permissions;
- catches Google's answer on `127.0.0.1`, checks the account with `users/me/profile`, and
  stores everything in `deploy/docker-compose/.env`;
- writes `gmail.env`, starts `gmail-mcp`, and registers `gmail` with the agent.

| Option | Effect |
|---|---|
| `--account NAME` | What RockBot calls the account (lower-case letters, digits, `-`) |
| `--email ADDRESS` | Pre-selects the account on Google's page and refuses a different one |
| `--reconsent` | Sign in again, e.g. after revoking access |
| `--render-only` | Regenerate `gmail.env` from `.env` and restart `gmail-mcp` |

These `.env` keys need to go into chezmoi, or whatever manages your `.env`:
- `GMAIL_CLIENT_ID`, `GMAIL_CLIENT_SECRET` and `GMAIL_ACCOUNTS`;
- one `GMAIL_<NAME>_EMAIL` and `GMAIL_<NAME>_REFRESH_TOKEN` per account;
- `gmail` added to `COMPOSE_PROFILES`.

`gmail.env` is generated and git-ignored. It only *references* those keys, so after the tool
rewrites `.env`, run `setup.sh --render-only`.

To remove one account, run `deploy/gmail/teardown.sh --account NAME`. To remove all accounts and
the stored client, run `teardown.sh --all`. Teardown revokes the tokens with Google.

## 3. Workspace accounts run by someone else

An employer's or client's Google Workspace admin decides which third-party apps can use Gmail.
If consent fails with **"Access blocked: RockBot has not completed the Google verification
process"**, `admin_policy_enforced` or `access_denied`, the admin has to trust the app. Send them:

- the **Client ID** from step 1.4;
- the request: *Admin console > Security > Access and data control > API controls > Manage
  third-party app access > Add app > OAuth App Name Or Client ID*. Paste the ID and set access to
  **Trusted** (or **Limited** with Gmail allowed).

The personal account isn't affected by this. If the admin says no, RockBot can't use that
account through this server.

## Security

Email is untrusted input: anyone can send you text meant to steer the agent. The tool descriptions
say so, and the server's limit is hard: the worst a steered agent can do is write a draft, which
you see before anything is sent.
