---
name: dependency-audit
description: Check RippleEngine dependencies (NuGet and the Dashboard/docs npm trees) for known vulnerabilities and pinning/supply-chain hygiene before packing or publishing. Use when reviewing dependencies, preparing a release, bumping packages, or asked about security of the dependency graph.
---

# Dependency / supply-chain audit

RippleEngine ships seven NuGet packages (Core, Storage, Engine, MartenDb, EntityFrameworkCore, Dashboard,
Hosting); the Dashboard package embeds an Angular bundle, and the docs site is a Node project. Tests pull the
`postgres:17` container image. The realistic surface is the dependency graph and the pack/publish path.

## NuGet

```bash
dotnet restore x86cc.RippleEngine.slnx
dotnet list x86cc.RippleEngine.slnx package --vulnerable --include-transitive
dotnet list x86cc.RippleEngine.slnx package --outdated
dotnet list x86cc.RippleEngine.slnx package --deprecated
```

For a transitive vulnerability, name the top-level package pulling it in before proposing a bump.

Version constraints to respect (see `AGENTS.md`):
- **CPM** — versions live only in `Directory.Packages.props`; a `Version=` on a `PackageReference` is `NU1008`.
- **EF Core stays on 9.x** so Npgsql stays on the 9.0.x line Marten needs. Don't bump EF/Npgsql to 10 casually.
- **Aspire pinned to `13.4.6`**; `Microsoft.Extensions.*` must stay `>= 10.0.8`.

## npm (Dashboard SPA + docs)

```bash
(cd x86cc.RippleEngine.Dashboard/spa && npm audit --omit=dev)
(cd docs && npm audit --omit=dev)
```

Runtime (shipped) findings in the SPA matter most — it's embedded in the Dashboard package.

## Pinning & source hygiene

- Flag floating (`*`) versions anywhere; confirm lockfiles (`package-lock.json`) are committed.
- Note whether a `global.json` pins the SDK and a `nuget.config` pins sources (neither exists today — report it).
- CI action versions (`actions/*@v4`) — note if not SHA-pinned.

## Release path

- CI's `Pack` step builds every `IsPackable` project on each push/PR; a `v*` tag publishes with
  `secrets.NUGET_API_KEY`. The `nuget-trusted-publishing` skill covers moving that to OIDC.
- `dotnet nuget push` is denied for agents in `.claude/settings.json`; publishing is a deliberate human action.

## Reporting

Short report: vulnerable/deprecated packages (severity + fix), floating versions, pinning status. Recommend
fixes; don't bump versions without surfacing the change first.
