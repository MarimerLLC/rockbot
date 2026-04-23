---
title: NuGet release
nav_order: 14
---

# NuGet Package Release Process

## Overview

RockBot SDK packages are published to nuget.org via a GitHub Actions workflow triggered by **release branches**. The version number is derived from the branch name — no manual version editing required.

## Prerequisites

- A `NUGET_API_KEY` secret configured in the GitHub repository settings (Settings > Secrets and variables > Actions)
- Push access to create branches in the repository

## How to Release

### 1. Create a release branch from main

The branch name determines the version. The `release/v` prefix is stripped to produce the version string.

```bash
# Stable release
git checkout main && git pull
git checkout -b release/v0.4.0
git push origin release/v0.4.0

# Pre-release
git checkout main && git pull
git checkout -b release/v0.4.0-beta001
git push origin release/v0.4.0-beta001
```

### 2. The workflow runs automatically

Pushing a `release/v*` branch triggers `.github/workflows/release.yml`, which runs these steps **sequentially** — a failure at any step stops the pipeline:

1. **Extract version** — parses `0.4.0` (or `0.4.0-beta001`) from the branch name
2. **Build** — `dotnet build` with `-p:Version=0.4.0`
3. **Test** — runs all unit tests; failing tests block the release
4. **Pack** — produces 25 `.nupkg` files in `./nupkgs/`
5. **Push to NuGet** — publishes each package to nuget.org (`--skip-duplicate`)
6. **Create GitHub Release** — tags the commit as `v0.4.0`, attaches the `.nupkg` files, and auto-generates release notes

### 3. Verify

- Check the [Actions tab](https://github.com/MarimerLLC/rockbot/actions/workflows/release.yml) for workflow status
- Check [nuget.org](https://www.nuget.org/profiles/Marimer) for the published packages (may take a few minutes to index)
- Check the [Releases page](https://github.com/MarimerLLC/rockbot/releases) for the created release

## Version Format

| Branch name | Package version | Assembly version |
|---|---|---|
| `release/v0.4.0` | `0.4.0` | `0.4.0.0` |
| `release/v0.4.0-beta001` | `0.4.0-beta001` | `0.4.0.0` |
| `release/v1.0.0-rc.1` | `1.0.0-rc.1` | `1.0.0.0` |

The .NET SDK automatically strips prerelease suffixes for `AssemblyVersion` and `FileVersion`.

## Local Testing

You can test package creation locally without publishing:

```bash
# Default version (from build.number file, falls back to 0.3.0)
./pack.sh

# Specific version
./pack.sh 0.4.0-beta001
```

Packages are written to `./nupkgs/` (gitignored).

To test a push to nuget.org manually:

```bash
./push.sh YOUR_NUGET_API_KEY
```

## What Gets Packaged

25 SDK library projects are packaged. Executable apps, MCP servers, Blazor UI, sample agents, and test projects are excluded.

The packaged projects are all `.csproj` files under `src/` that have `<IsPackable>true</IsPackable>`. This is controlled per-project, with the default set to `false` in `Directory.Build.props`.

## How Versioning Works

- `Directory.Build.props` sets a default `Version` of `0.3.$(BuildNumber)` using a local `build.number` file (gitignored)
- The release workflow overrides this with `-p:Version=X.Y.Z` from the branch name
- All NuGet metadata (Authors, License, RepositoryUrl, etc.) is shared via `Directory.Build.props`
- Each package includes `NUGET_README.md` as its README on nuget.org

## Files Involved

| File | Purpose |
|---|---|
| `.github/workflows/release.yml` | Release workflow — build, test, pack, publish, create GitHub Release |
| `.github/workflows/ci.yml` | CI workflow — build and test only (main/PR branches) |
| `Directory.Build.props` | Shared version and NuGet metadata |
| `NUGET_README.md` | README included in all NuGet packages |
| `pack.sh` | Local pack script (optional version argument) |
| `push.sh` | Local push script (takes NuGet API key) |
