---
title: Home
layout: home
nav_order: 1
description: "RockBot — an event-driven autonomous agent framework built on .NET."
permalink: /
---

# RockBot
{: .fs-9 }

An event-driven autonomous agent framework for .NET — message-based, process-isolated, and built on the principle that *nothing trusts the LLM*.
{: .fs-6 .fw-300 }

[Get started on Docker Desktop](getting-started-docker-desktop){: .btn .btn-primary .fs-5 .mb-4 .mb-md-0 .mr-2 }
[View on GitHub](https://github.com/MarimerLLC/rockbot){: .btn .fs-5 .mb-4 .mb-md-0 }

---

## What is RockBot?

RockBot is a framework for building **multi-agent AI systems** where agents communicate exclusively through a message bus. There is no shared memory, no direct method calls between agents, and no LLM-generated code running in-process with the host.

Agents built with the RockBot framework SDK are designed around the principle of least privilege: an agent should not have access to any secrets other than LLM keys. All other secrets live in MCP servers or other isolated services.

Each agent is an isolated process that reacts to messages, invokes tools, calls LLMs, delegates work to other agents, and emits responses — all via a topic-based pub/sub message bus backed by RabbitMQ (or an in-process bus for local development).

## Documentation

- [Getting started on Docker Desktop](getting-started-docker-desktop)
- [Messaging](messaging) — envelopes, transports, topic conventions
- [Agent host](agent-host) — handler pipeline, profile loading, system prompt composition
- [Subagents](subagents) — isolated LLM loops with progress reporting
- [Memory](memory) — conversation, long-term, and working memory
- [Skills](skills) — learned skill storage, BM25 recall, dream-based optimization
- [Tools](tools) — tool registry, invocation dispatch, tool-guide discovery
- [A2A](a2a) — agent-to-agent task delegation
- [Wisps](wisps) — short-lived worker pattern
- [Dream service](dream-service) — offline self-optimization
- [Knowledge graph](knowledge-graph)
- [Blazor UI](blazor-ui)
- [NuGet release](nuget-release)

## Community

- [Discord server](https://discord.gg/eQjxWG6KYN)
- [GitHub Issues](https://github.com/MarimerLLC/rockbot/issues)

## License

RockBot is released under the [MIT license](https://github.com/MarimerLLC/rockbot/blob/main/LICENSE).
