# Ainur

Ainur is an agent orchestrator for autonomous teams working on substantial projects over long periods. Each project has an organization of managers and specialists, persistent agent identities, owned objectives, and visible dollar costs. The user works through the team's manager.

The initial target is a local C# and .NET service with a React and TypeScript web UI that can develop, verify, and autonomously upgrade itself. Agents have dedicated execution threads and run tools inline, including C# tools and embedded PowerShell pipelines that can pass live .NET objects.

The project is currently in design. The [draft project and runtime specification](docs/spec.md) records the product decisions, proposed architecture, and implementation milestones.
