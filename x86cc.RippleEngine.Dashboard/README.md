# Ripple Dashboard

The monitoring surface for Ripple — waves → ripples → splashes — as a single package: the read-only API
(`DashboardApi.MapRippleDashboard()`, mounted at `/api`) and the Angular + Tailwind SPA that consumes it.

The SPA is **embedded in this assembly**. `spa/` is built into `dist/` during `dotnet build` and embedded as
resources, so a host serves the whole dashboard from a package reference — there is no `wwwroot` to deploy and
nothing to copy into a container image.

## Using it

Turn it on in the engine setup (`x86cc.RippleEngine.Hosting`):

```csharp
builder.AddRippleEngine(o => o.EnableDashboard = true);
```

The API lands on `/api` and the SPA on the root, mapped as a route **fallback** so it never shadows the host's
own endpoints. To place the API yourself — a different prefix, behind authorization — leave the flag off and
call `MapRippleDashboard()` where you want it.

## Building the SPA

Node 20+ and npm. The .NET build runs it for you (and skips with a warning when npm isn't on PATH):

```bash
dotnet build x86cc.RippleEngine.Dashboard/x86cc.RippleEngine.Dashboard.csproj
dotnet build x86cc.RippleEngine.slnx -p:BuildSpa=false     # skip it explicitly
```

Or drive it directly from `spa/`:

```bash
cd spa
npm ci
npm run build      # -> ../dist, which the csproj embeds
```

## Developing the SPA (`ng serve`)

```bash
cd spa
npm start          # ng serve --proxy-config proxy.conf.json  → http://localhost:4200
```

`proxy.conf.json` forwards `/api` to a running worker (default `http://localhost:5200`); point its `target` at
whatever URL your engine host is on — when launched via the Aspire AppHost, copy the worker's URL from the
Aspire dashboard.

## Structure

- `DashboardApi.cs` — the `/api` read projections (waves, activity/histogram, per-type metrics, cluster,
  report CSV, and the `type_schedule` settings incl. pause/resume).
- `RippleDashboardSpa.cs` — serves the embedded bundle.
- `spa/src/app/core` — `models.ts`, `ripple-api.service.ts`, `engine-activity.service.ts`.
- `spa/src/app/pages` — `waves` (list, detail, timeline, heatmap), `metrics`, `settings`, `wave-range`.
