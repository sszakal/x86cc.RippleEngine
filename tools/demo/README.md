# Demo recording

Scripts that record the sample overview video embedded in the docs (`docs/static/video/ripple-demo.mp4`).

`record.mjs` drives Swagger and the dashboard with Playwright and captures Chrome's screencast frames at 1.5× scale
(1920×1200). It injects a visible cursor, captions and title cards into the page. `build.sh` turns the frames into a
30 fps H.264 mp4 with ffmpeg.

The storyline: list the seeded tax codes, trigger a 200k-ripple corporate-tax wave and a 100k-ripple employee-tax
wave from Swagger, then on the dashboard show the cluster and scheduling shares, drill down to the waves, reschedule
the corporate-tax wave to a bigger share, pause the employee-tax type and resume it "from now", and fast-forward to
both waves completing. The fast-forward stops the screencast while the waves drain and cuts that gap out of the video.

Requirements: Node 20+, Chromium (default `/usr/bin/chromium`; set `CHROMIUM` for another path), `ffmpeg` and
`python3`. The sample must be running (`dotnet run --project x86cc.Ripple.Sample.AppHost`).

```bash
cd tools/demo
npm ci
npm run record       # records into out/, then replaces the docs video and its poster image
```

If `TAX-200K` or `TAX-100K` has no companies, the script first seeds 311,000 companies through `POST /seed`, before
recording starts. Each run adds two waves. Start from a fresh database for a clean wave history.

Environment overrides:

| Variable | Default | Meaning |
|---|---|---|
| `API` | `http://localhost:5100` | the sample WebAPI (Swagger) |
| `DASH` | `http://localhost:5200` | any worker's dashboard |
| `PACE` | `0.85` | scales the pauses |
| `CORP_CODE` / `EMP_CODE` | `TAX-200K` / `TAX-100K` | the tax codes the two waves fan out over |

The waves must stay live for about 45 s of reschedule/pause/resume. At the sample's ~2k ripples/s on a laptop,
300k ripples are enough. On a much faster machine, set `CORP_CODE=TAX-400K` (the script then seeds 711,000 companies).

The storyline (steps, captions, timing) lives in `record.mjs`; edit it there and re-run.

The recording uses the dashboard's dark theme. Swagger UI has no dark theme, so `record.mjs` darkens it with an inverted-colour CSS filter.
