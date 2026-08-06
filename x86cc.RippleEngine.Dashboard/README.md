# Ripple Dashboard

Angular + Tailwind (TailAdmin-style) UI for Ripple: jobs → tasks → task runs.

It consumes the read API exposed by `x86cc.RippleEngine.Scheduler` (`MapRippleApi`, default base
`/api/fanout`), served by `x86cc.RippleEngine.Sample.Scheduler`.

## Prerequisites

Node 20+ and npm. `npm install` once.

## Develop (`ng serve`)

```bash
npm install
npm start          # ng serve --proxy-config proxy.conf.json  → http://localhost:4200
```

`proxy.conf.json` forwards `/api` to the scheduler. Set its `target` to your running scheduler URL
(when launched via the Aspire AppHost, copy the scheduler's https URL from the Aspire dashboard;
default placeholder is `https://localhost:7100`).

## Production build (served by the Scheduler)

`ng build` emits straight into `../x86cc.RippleEngine.Sample.Scheduler/wwwroot` (configured in
`angular.json`), so the scheduler serves the SPA and the API on one origin.

```bash
npm run build
```

Or build it as part of the .NET build (opt-in, requires Node):

```bash
dotnet build x86cc.RippleEngine.slnx -p:BuildDashboard=true
```

Then run the Aspire AppHost and open the scheduler's root URL.

## Structure

- `src/app/core` — `models.ts`, `fanout-api.service.ts` (HttpClient → `/api/fanout/...`).
- `src/app/app.component.ts` — TailAdmin-style shell (sidebar + header).
- `src/app/pages` — `jobs-list` (stats + jobs), `job-tasks` (paginated, state filter),
  `task-runs` (payload, runs, output/errors).
