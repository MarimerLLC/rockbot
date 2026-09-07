# McpServer.BinaryFixture

An MCP server that returns binary content in every shape RockBot's bridge has to cope with, so
those paths can be exercised against a live agent instead of only in unit tests.

## Why it exists

The MCP bridge captures binary content out of tool responses, and `analyze_file` hands image
bytes to a vision model. Both are unit-tested — but no server in a normal deployment returns the
shapes they exist for. The ones RockBot talks to either write files to disk or corrupt their
bytes on the way out. That left the interesting paths verifiable only in isolation, which is how
the mangled-binary case in issue #513 stayed hidden until someone smoke-tested capture against a
real server.

This server closes that gap, and doubles as a fixture for future bridge work.

## Tools

| Tool | Shape returned | What it proves |
|---|---|---|
| `get_image` | Typed `image` content block (PNG) | Capture's no-configuration path |
| `get_audio` | Typed `audio` content block (WAV) | Same path, non-image media |
| `get_image_with_text` | Text block + image block | Only the image is rewritten |
| `get_file_base64` | `{name, path, sha, size, encoding, content}` — a repository server's shape. `kind=image` or `kind=text` | The declarative capture rule fires for the image and declines for the text |
| `get_file_mangled` | Same shape, but content is the PNG's bytes decoded as UTF-8 | The mangled-binary guard: field dropped and explained, not flooded into context |
| `get_text` | Plain text | Control — never captured |
| `describe_fixtures` | The fixture image's known description | Checking a vision model's answer against a known one |

## The fixture image

A 240×120 PNG generated in code: three vertical bars, left to right red (medium), green
(tallest), blue (shortest), on a dark baseline. Generated rather than committed so the repository
carries no binary blobs — but the real reason is that the content is *known*, so a claim about
what a vision model saw can be checked against `TestMedia.BarChartDescription` instead of against
someone's impression of a photo.

The fixtures are also served over plain HTTP for inspection, which answers "what was actually in
the file" without reproducing the MCP call:

```
GET /fixtures/chart.png     the image itself
GET /fixtures/tone.wav      the audio
GET /fixtures/expected      the image's documented description
GET /health                 readiness
```

## Running it

Locally:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project src/McpServer.BinaryFixture
curl http://127.0.0.1:5199/fixtures/expected
```

In-cluster, for a live test against the agent:

```bash
VERSION=$(grep -oP '<Version>\K[^<]+' Directory.Build.props)
docker build -f src/McpServer.BinaryFixture/Dockerfile \
  -t rockylhotka/rockbot-binary-fixture-mcp:$VERSION .
docker push rockylhotka/rockbot-binary-fixture-mcp:$VERSION

kubectl apply -f deploy/k8s/mcp-binary-fixture.yaml
```

Then register it in the agent's `mcp.json` on the PVC, with a capture rule so the base64 tools
are covered as well as the typed blocks:

```json
"binary-fixture": {
  "type": "sse",
  "url": "http://mcp-binary-fixture.rockbot.svc.cluster.local/",
  "attachments": {
    "capture": {
      "rules": [
        {
          "tools": ["get_file_base64", "get_file_mangled"],
          "contentField": "content",
          "nameField": "name",
          "encodingField": "encoding"
        }
      ]
    }
  }
}
```

`get_image`, `get_audio`, and `get_image_with_text` need no rule — typed blocks are captured
without configuration.

## Cleaning up

```bash
kubectl delete -f deploy/k8s/mcp-binary-fixture.yaml
```

and remove the `binary-fixture` entry from `mcp.json`. Nothing else is left behind: the server
holds no state, reads no credentials, and makes no outbound calls.
