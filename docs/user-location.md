---
title: User location
nav_order: 16
---

# User location

RockBot can follow where you are, using your phone's location. It's optional; with
nothing configured, the agent behaves exactly as it does without location. This
guide covers a full setup: the phone app, the server that stores the data, the agent
settings, the optional MCP server for history questions, and **regions** for named
places like home and work.

```
OwnTracks app ──HTTP──▶ OwnTracks Recorder ◀──HTTP── RockBot agent      (current city, time zone)
  (phone)                 (stores history)   ◀──HTTP── owntracks-mcp ◀──MCP── RockBot agent
                                                       (optional: history, stays, distance, regions)
```

## What you get

**Built in** (`WithUserLocation`, see [Agent host](agent-host.md#user-location-and-timezones)):

- The agent polls the Recorder every 5 minutes. When you travel, its clock follows
  your time zone, while your home zone stays what `Agent:Timezone` / `set_timezone`
  says.
- Every turn's date/time line includes your last known **city** and when it was
  reported. Precise coordinates never go into the prompt.
- Scheduled tasks can be `local` (follow you, e.g. a morning brief), `home` (stay on
  home time) or pinned to a zone.

**With the MCP server** (optional): the agent can answer history questions such as
"where was I yesterday afternoon?", "how long was I at the office?", "how far did I
drive this week?" and "am I at home?".

## Privacy: decide before you start

- **The Recorder has no authentication.** Anyone who can reach its port can read
  your whole history and post fake points. Put it on a private network (a VPN such
  as Tailscale or WireGuard), or behind a reverse proxy with HTTP basic auth (the
  phone app supports it). Never expose it directly to the internet.
- **The built-in feature is city-level.** The agent's prompt gets your city, or
  coordinates rounded to about 10 km when there's no geocoder.
- **The MCP server is full detail.** Its tools return street addresses, exact
  coordinates, speed, battery, Wi-Fi network name and full history. Tool results
  can end up in the agent's memory like any other conversation content. If that's
  more than you want, use only the built-in feature.

## 1. Run the OwnTracks Recorder

The [OwnTracks Recorder](https://github.com/owntracks/recorder) receives location
reports over HTTP, keeps them on disk, and serves them back through a REST API
(`/api/0/...`). No MQTT broker is needed.

Docker:

```bash
docker run -d --name owntracks-recorder \
  -p 8083:8083 \
  -e OTR_PORT=0 \
  -e OTR_HTTPPORT=8083 \
  -v owntracks-store:/store \
  owntracks/recorder:1.0.4-47
```

- `OTR_PORT=0` turns off MQTT. The log should say `Not using MQTT: disabled by port=0`.
- `/store` holds everything: the monthly `.rec` logs, the latest fix per device, and
  the geocode cache. Use a persistent volume; it's the only copy of your history.
- Don't mount anything at `/config`. The image keeps its config and time-zone
  database there.
- On Kubernetes: one replica with `strategy: Recreate` (the store volume is
  ReadWriteOnce), and a ClusterIP Service on 8083. Expose it to the phone only
  through your private network. The image runs fine as a non-root user with a
  read-only root filesystem, as long as `/store` and `/tmp` are writable.

### Street addresses (optional)

Without a geocoder, the Recorder stores coordinates and the time zone, but no
addresses or city names. To add them, get an [OpenCage](https://opencagedata.com/) API
key (the free tier is plenty for one phone) and set:

```bash
-e OTR_GEOKEY='opencage:<your-key>'
```

Restart the Recorder after adding it. Points recorded before the key existed stay
without addresses. Lookups are cached per geohash cell (about 150 m × 150 m), so
places you return to cost nothing.

## 2. Set up the phone

Install **OwnTracks** ([Android](https://play.google.com/store/apps/details?id=org.owntracks.android),
[iOS](https://apps.apple.com/app/owntracks/id692424691)). The steps below are for
Android; iOS has the same settings in slightly different places.

1. Grant location permission **Allow all the time**.
2. Turn off battery optimization for OwnTracks. Otherwise Android stops it in the
   background and reports simply stop.
3. **Preferences → Connection**
   - Mode: **HTTP**
   - URL: `https://<your-recorder-host>/pub`
   - Identification: **Username** (e.g. `alice`), **Device ID** (e.g. `phone`),
     and a two-letter Tracker ID. The Recorder files data under `<user>/<device>`,
     and RockBot is configured with the same names.
   - Authentication: only if you put a proxy with basic auth in front.
4. **Monitoring mode** (the icon in the app's toolbar): choose **Significant
   changes**. It's battery friendly and reports when you move a meaningful distance.
   **Move** reports much more often while moving. **Manual** sends nothing until you
   tap publish, so history stays empty (see [Troubleshooting](#troubleshooting)).
5. If the Recorder is on a VPN, set the VPN as **always-on** on the phone. OwnTracks
   queues reports while it can't connect and sends them later.
6. Tap publish once, then check the Recorder has the fix:

```bash
curl -s "http://<recorder>:8083/api/0/last?user=alice&device=phone"
```

The reply includes `tst` (Unix time of the fix), `lat`/`lon`, `acc` (metres) and
`tzname`. With a geocoder it also has `addr` and `cc`.

## 3. Turn on location in RockBot

Set the Recorder URL and user in the agent's configuration:

```json
{
  "Agent": { "Timezone": "America/Chicago" },
  "UserLocation": {
    "OwnTracks": {
      "BaseUrl": "http://owntracks-recorder:8083",
      "User": "alice",
      "Device": ""
    }
  }
}
```

With the Helm chart, the same settings are in the values file:

```yaml
agent:
  timezone: "America/Chicago"          # your home zone
  userLocation:
    owntracks:
      baseUrl: http://owntracks-recorder.owntracks.svc.cluster.local:8083
      user: alice
      device: ""                       # empty = newest fix across your devices
```

`Agent:Timezone` stays your **home** zone, and the location sets your **current**
zone. All keys (poll interval, max fix age) are listed under
[Configuration reference](agent-host.md#configuration-reference).

Check the agent log after it starts:

```
User location: polling every 00:05:00
User location: zone America/Chicago (Springfield), fix age 00:03:12.4410000
```

`(no locality)` in place of a city means the Recorder has no geocoder, or its
geocoder returned no city for that spot. The agent then gets rounded coordinates.

## 4. Optional: the location MCP server

The built-in feature only knows your *latest* fix. For history, add
[OwnTracksHermes](https://github.com/de-niji/OwnTracksHermes) (MIT), a read-only MCP
server over the Recorder API.

| Tool | Answers |
|---|---|
| `get_last_location` | Where am I now: address, regions, age, accuracy, battery, map link |
| `get_location_history` | Where was I between X and Y: stays (where and how long), distance, top speed, a thinned-out route |
| `get_distance` | Straight-line distance from the latest position to a point |
| `list_devices` | Users and devices in the Recorder |
| `reverse_geocode` | An address from the Recorder's geocode cache |
| `owntracks_status` | Connection check and active configuration |

It also has three reminder tools (`create_location_reminder`, `list_location_reminders`,
`delete_location_reminder`). They depend on a cron job in the Hermes agent and don't
work with RockBot, so deny them (below).

### Build the image

Upstream publishes no image. `deploy/owntracks-mcp/` builds one from a pinned commit:

```bash
docker buildx build --platform linux/amd64 \
  -t <registry>/owntracks-mcp:0.2.0-9f005a7 --push deploy/owntracks-mcp
```

The image runs `serve.py`, a small launcher. Without it, every request through a
Kubernetes Service or other hostname gets **`421 Invalid Host header`**. Upstream's
MCP SDK setup only accepts `localhost` as the `Host` header, and its `--host` flag
doesn't change that. The launcher keeps that protection on and adds the hostnames
in `ALLOWED_HOSTS` (comma-separated `host:port` patterns, `*` for any port). The
default fits a Service named `owntracks-mcp` in namespace `owntracks`; set
`ALLOWED_HOSTS` if yours differs.

### Run it

Environment variables:

| Variable | Example | Meaning |
|---|---|---|
| `OWNTRACKS_URL` | `http://owntracks-recorder:8083` | Recorder base URL (required) |
| `OWNTRACKS_TIMEZONE` | `America/Chicago` | Zone for parsing and showing times |
| `OWNTRACKS_DEFAULT_USER` | `alice` | Who "me" means |
| `OWNTRACKS_DEFAULT_DEVICE` | `phone` | Default device |
| `OWNTRACKS_HOME` | `51.96,7.62` | Optional single home point for `get_distance` with no target. See [Regions](#regions-home-work-and-other-places) if you have more than one home |

A minimal Kubernetes Deployment and Service:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: owntracks-mcp
  namespace: owntracks
spec:
  replicas: 1                      # MCP sessions live in memory
  selector:
    matchLabels: { app: owntracks-mcp }
  template:
    metadata:
      labels: { app: owntracks-mcp }
    spec:
      securityContext: { runAsNonRoot: true, runAsUser: 1000, runAsGroup: 1000 }
      containers:
        - name: mcp
          image: <registry>/owntracks-mcp:0.2.0-9f005a7
          env:
            - { name: OWNTRACKS_URL, value: "http://owntracks-recorder.owntracks.svc.cluster.local:8083" }
            - { name: OWNTRACKS_TIMEZONE, value: "America/Chicago" }
            - { name: OWNTRACKS_DEFAULT_USER, value: "alice" }
            - { name: OWNTRACKS_DEFAULT_DEVICE, value: "phone" }
            - { name: HOME, value: /home/owntracks }
          ports: [{ name: mcp, containerPort: 8765 }]
          readinessProbe: { tcpSocket: { port: mcp } }   # /mcp only answers MCP POSTs
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          volumeMounts:
            - { name: home, mountPath: /home/owntracks }
            - { name: tmp, mountPath: /tmp }
      volumes:
        - { name: home, emptyDir: {} }
        - { name: tmp, emptyDir: {} }
---
apiVersion: v1
kind: Service
metadata:
  name: owntracks-mcp
  namespace: owntracks
spec:
  selector: { app: owntracks-mcp }
  ports: [{ name: mcp, port: 8765, targetPort: mcp }]
```

Like the Recorder, it has no authentication. Keep it internal (no Ingress).

### Connect RockBot

Add an entry to the agent's `mcp.json` (on the data volume, e.g.
`/data/agent/mcp.json`). The MCP bridge reloads the file by itself, so the agent
doesn't need a restart:

```json
{
  "mcpServers": {
    "owntracks": {
      "type": "sse",
      "url": "http://owntracks-mcp.owntracks.svc.cluster.local:8765/mcp",
      "transportMode": "streamable-http",
      "deniedTools": [
        "create_location_reminder",
        "list_location_reminders",
        "delete_location_reminder"
      ]
    }
  }
}
```

`type: "sse"` only marks it as an HTTP server; `transportMode` picks the protocol.
The agent log should show:

```
Connected to MCP server owntracks (impl=owntracks v1.30.0) with 6 tools, 0 prompts and 0 resources
```

## Regions: home, work and other places

A **region** is a named circle you define in the OwnTracks app, such as "Home",
"Work", "Gym" or "Cabin". Regions are the best way to tell the agent about the
places that matter to you:

- While you're inside a region, every location report carries its name (`inregions`).
  `get_last_location` returns it as `in_regions`, and each stay in
  `get_location_history` lists its `regions`. So the agent can answer "am I at
  work?", "when did I get home?" or "how long was I at the gym?" by name, instead of
  guessing from an address.
- Entering or leaving a region sends a `transition` event right away, without waiting
  for the next regular report. That makes arrival and departure times much more
  accurate than movement-based reports alone.
- The places stay on your phone. Nothing about them goes into RockBot's
  configuration or memory unless a conversation puts it there.

**Set one up (Android):** open the app's menu → **Regions** → **+**. Give it a name,
set the center (your current location, or pick it on the map) and a radius. 100–200
m suits a house or office; use more for a large site. Name regions the way you'd say
them, because that's the name the agent sees and repeats back. Regions work in
**Significant changes** and **Move** modes.

**More than one home:** create a region for each ("Home", "Lake house", "Apartment").
Don't set `OWNTRACKS_HOME`, which can hold only one point. "Which home am I at?" is
answered by the region name, and "how far am I from the lake house?" works by
naming the place: the agent looks up its coordinates and calls `get_distance`.

**A home that moves** (an RV, a boat): a fixed region doesn't fit. Either redefine
the region when you park, or just tell the agent where it is. If you want it tracked
automatically, a spare phone left in the vehicle running OwnTracks as a second
device works. The tools handle several devices, so "how far am I from the RV?"
becomes the distance between two devices. In that case, set `UserLocation:OwnTracks:Device`
to your own phone, so the agent's time zone follows you and not the vehicle.

## Troubleshooting

**History is empty, or has only a few points.** Check the monitoring mode. Each
stored report has an `m` field: `1` is Significant changes, `2` is Move, and `0` is
Manual (the app sends nothing unless you tap publish). A `t` field of `u` marks a
manual publish. To see the mode of recent reports:

```bash
grep -o '"m":-\?[0-9]*' /store/rec/<user>/<device>/$(date +%Y-%m).rec | sort | uniq -c
```

If the mode is right but there are gaps, check that battery optimization is off and
the VPN is always-on.

**The log says `(no locality)`.** The Recorder has no `OTR_GEOKEY`, or the
geocoder returned no city for that spot. With a key, the agent takes the city from
the address when there's no separate city field.

**`421 Invalid Host header` from the MCP server.** The hostname RockBot uses isn't
in `ALLOWED_HOSTS`. Add it, e.g. `my-mcp.example.internal:*`.

**The agent says it can't know where you are, though the log shows a fix.** Older
builds had a completion evaluator that didn't see the location line, so it rejected
correct answers and made the agent take them back. Update to a build that includes
the fix (#748).

**Pods can't reach a `*.ts.net` (Tailscale) address.** By default, cluster DNS sends
MagicDNS names to public DNS. Plain tailnet names don't resolve there, and Funnel
names resolve to public relays, which may not be reachable from your own network.
If the nodes run Tailscale, forward your tailnet's domain to MagicDNS. On k3s, add
a `coredns-custom` ConfigMap in `kube-system`, then restart CoreDNS:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: coredns-custom
  namespace: kube-system
data:
  tailnet.server: |
    <your-tailnet>.ts.net:53 {
        errors
        cache 30
        forward . 100.100.100.100
    }
```
