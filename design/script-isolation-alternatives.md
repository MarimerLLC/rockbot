# Script Isolation Alternatives

Research into non-Kubernetes options for running ephemeral Python scripts in an isolated, constrained environment. The goal is to identify approaches suitable for users who don't have (or don't want) a Kubernetes cluster, while still providing meaningful isolation and fast startup.

## Context

The existing `RockBot.Scripts.Container` implementation runs each script in an ephemeral K8s pod. This provides strong isolation:

- Separate Linux network namespace (no outbound access by default)
- Read-only root filesystem option
- CPU and memory hard limits via cgroups
- Automatic pod lifecycle (never restarts, killed on deadline)
- No access to K8s service account or host filesystem

However, it requires a working Kubernetes cluster. This document explores alternatives ordered roughly from most to least infrastructure-heavy.

---

## Option 1: Docker without Kubernetes (recommended for near-K8s parity)

**Implementation project**: `RockBot.Scripts.Docker` (future)

Run scripts in ephemeral Docker containers via the Docker Engine API or CLI, without needing K8s at all.

```
docker run --rm \
  --network=none \
  --cpus=0.5 --memory=256m \
  --read-only --tmpfs /tmp \
  -e ROCKBOT_SCRIPT="..." \
  -e ROCKBOT_INPUT="..." \
  python:3.12-slim \
  sh -c 'python -c "$ROCKBOT_SCRIPT"'
```

**Isolation properties:**

| Property | K8s pod | Docker (no K8s) |
|---|---|---|
| Separate process namespace | ✅ | ✅ |
| Network isolation (`--network=none`) | ✅ | ✅ |
| CPU hard limit | ✅ | ✅ |
| Memory hard limit | ✅ | ✅ |
| Read-only filesystem | ✅ | ✅ |
| No host filesystem access | ✅ | ✅ |
| Kubernetes required | ✅ | ❌ |

**Startup time**: ~300–800 ms for a warm `python:3.12-slim` image (image must be pulled on first run). Subsequent runs on the same host hit the local image cache.

**Pip packages**: Same approach as K8s — `pip install --target /tmp/pypackages` before script, `PYTHONPATH=/tmp/pypackages`.

**Implementation approach**: Use `System.Diagnostics.Process` to invoke `docker run` directly, or use the [Docker SDK for .NET](https://github.com/dotnet/Docker.DotNet) (`Docker.DotNet` NuGet package) for a proper API client. The CLI approach (`docker run`) is simpler and doesn't add a package dependency.

**Requirements**: Docker Desktop (Windows/macOS), Docker Engine (Linux), or any OCI-compatible runtime (Podman with Docker CLI compatibility enabled).

**Security caveats**: The Docker daemon socket grants root-equivalent access on the host. Mounting `/var/run/docker.sock` into a container (or exposing it over TCP without TLS) is a privilege escalation vector. If `RockBot.Scripts.Docker` runs inside a container, use rootless Docker or a dedicated runner VM.

---

## Option 2: Process-based isolation (implemented — `RockBot.Scripts.Local`)

**Implementation project**: `RockBot.Scripts.Local` ✅

Run the Python interpreter as a child process directly on the host operating system. This is the lowest-infrastructure option and provides the fastest startup time (~20–50 ms on a warm system).

```csharp
var process = new Process
{
    StartInfo = new ProcessStartInfo("python")
    {
        Arguments    = $"-c \"{EscapeScript(script)}\"",
        // or: write to temp file and use: Arguments = tempScriptPath,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        WorkingDirectory       = tempWorkDir,
        Environment            = { ["ROCKBOT_SCRIPT"] = script, ["ROCKBOT_INPUT"] = input }
    }
};
```

**Isolation properties:**

| Property | K8s pod | Process |
|---|---|---|
| Separate memory space | ✅ | ✅ (OS process boundary) |
| Cannot read host process memory | ✅ | ✅ |
| Network isolation | ✅ | ❌ full host network |
| Filesystem isolation | ✅ | ❌ full host filesystem |
| CPU hard limit | ✅ | ⚠️ timeout only |
| Memory hard limit | ✅ | ⚠️ OS OOM killer |
| Kubernetes required | ✅ | ❌ |
| Docker required | ❌ | ❌ |
| Python required on host | ❌ | ✅ |

**Startup time**: ~20–80 ms (just spawning a process).

**Pip packages**: Run `pip install --quiet --target <tempDir> <packages>` as a preceding process. Set `PYTHONPATH=<tempDir>` on the script process. Packages are installed into a per-execution temp directory and deleted on cleanup.

**Timeout enforcement**: `CancellationTokenSource.CancelAfter(TimeoutSeconds)` + `process.Kill(entireProcessTree: true)` to kill the Python interpreter and any child processes it spawned.

**Resource limits** (optional enhancements):
- **Linux**: Wrap with `ulimit -v <mem_kb> -t <cpu_secs>` via a `sh -c 'ulimit ...; python ...'` command, or use systemd transient scopes (`systemd-run --scope --property=MemoryMax=...`).
- **Windows**: Use a Job Object via P/Invoke or a wrapper library to enforce memory limits.
- **Cross-platform**: `ResourcePolicy` in .NET 10 (if/when available) or a third-party sandbox library.

**Use case**: Development environments, low-risk scripts, trusted operators who are comfortable with the script running on the host OS.

---

## Option 3: MCP server as script executor

**Implementation project**: `RockBot.Scripts.Mcp` (future)

Deploy a persistent MCP server that exposes a `execute_python_script` tool. The MCP server can run anywhere — local, in a Docker container, on a remote VM — and clients call it over the MCP protocol (stdio or SSE/HTTP). The server itself handles the Python execution using whichever isolation primitive it chooses.

```
┌─────────────────────┐        MCP/stdio or SSE        ┌──────────────────────────┐
│   Agent Host        │ ──────────────────────────────► │  RockBot MCP Script      │
│  (RockBot.Agent, etc)│                                  │  Server                  │
└─────────────────────┘                                  │  (Docker container /     │
                                                         │   remote VM / local)     │
                                                         └──────────────────────────┘
```

**Isolation properties**: Depends on where and how the MCP server is deployed. If the MCP server runs in a Docker container, you get Docker-level isolation. If it's a remote VM, you get VM-level isolation. The agent host gets **protocol-level** isolation regardless — it cannot directly call Python, only send an MCP request.

**Startup time**: Near-zero for the client side (MCP call over an established connection). The server itself may use process-per-script or a warm Python interpreter pool. With a persistent warm pool, script startup can be sub-millisecond.

**Pros**:
- Deployers choose their isolation level (process, container, VM, remote)
- Multiple agents share a single execution endpoint
- Natural fit for the existing `RockBot.Tools.Mcp` MCP client infrastructure
- No Python on the agent host machine

**Cons**:
- Requires deploying and operating a separate MCP server
- Adds network latency (even if local, it's a round-trip)
- Persistent server is a larger attack surface than ephemeral execution

**Implementation**: Register the MCP server as an MCP tool provider in `RockBot.Tools.Mcp`. The existing `McpToolProvider` and `IToolRegistry` already support calling external MCP servers — this is just a specialized server deployment pattern, not new code in the agent host.

---

## Option 4: WASM sandbox (Wasmtime / Extism)

**Implementation project**: `RockBot.Scripts.Wasm` (future research)

Compile Python to WebAssembly using [Pyodide](https://pyodide.org) (CPython + WASM) and execute inside a Wasmtime or Extism host. WASM execution is sandboxed at the instruction level with no system calls allowed unless explicitly imported via WASI.

**Isolation properties:**

| Property | K8s pod | WASM (Wasmtime) |
|---|---|---|
| Memory isolation | ✅ | ✅ (linear memory model) |
| No syscalls | ✅ | ✅ (unless WASI imports declared) |
| No filesystem | ✅ | ✅ (by default) |
| No network | ✅ | ✅ (by default) |
| CPU limit | ✅ | ✅ (fuel/metering API) |
| Memory limit | ✅ | ✅ (configurable) |
| Kubernetes required | ✅ | ❌ |
| Docker required | ❌ | ❌ |

**Startup time**: Wasmtime JIT compilation adds ~100–500 ms on first run; subsequent runs with AOT or cached modules can be much faster.

**Pyodide considerations**: The Pyodide WASM bundle is ~12 MB (compressed) and loads in ~2–5 seconds cold, which is too slow for per-request use. The right model is a **persistent warm Pyodide interpreter** — reset interpreter state between scripts rather than loading from scratch each time.

**LLM code generation**: LLMs generate standard CPython code. Pyodide is CPython compiled to WASM, so most Python stdlib and pure-Python packages work. C-extension packages (numpy, pandas) require WASM-compiled wheels; Pyodide ships many popular ones.

**Status**: Promising but not production-ready for the use cases here. Recommend revisiting when Pyodide WASM startup improves or when a .NET-hosted Pyodide wrapper matures.

---

## Comparison Summary

| Option | Infrastructure | Startup | Filesystem isolation | Network isolation | CPU/mem limits | Recommendation |
|---|---|---|---|---|---|---|
| K8s pods (current) | Kubernetes cluster | ~2–5 s | ✅ | ✅ | ✅ (hard) | Production with K8s |
| Docker (no K8s) | Docker Engine | ~300–800 ms | ✅ | ✅ | ✅ (hard) | Production without K8s |
| Process (local) | Python on host | ~20–80 ms | ❌ | ❌ | ⚠️ (timeout) | Dev / low-risk |
| MCP server | MCP server deployment | ~1–5 ms (warm) | Depends | Depends | Depends | Flexible deployment |
| WASM (Pyodide) | None | ~100 ms+ (warm) | ✅ | ✅ | ✅ (metering) | Future |

---

## Decision

Two options are recommended for implementation, covering the two most common non-K8s scenarios:

1. **`RockBot.Scripts.Local`** (process-based) — implemented in this PR. Zero infrastructure, maximum development convenience. Suitable for development, experimentation, and low-risk operator environments.

2. **`RockBot.Scripts.Docker`** (Docker without K8s) — recommended as the next implementation. Provides near-K8s isolation without requiring a cluster. Use Docker SDK for .NET (`Docker.DotNet`) for proper lifecycle management.

The MCP server pattern is already supportable via the existing `RockBot.Tools.Mcp` infrastructure and doesn't require a new library project — it's a deployment concern. The WASM option is deferred pending maturity of the tooling.
