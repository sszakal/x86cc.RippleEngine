# Ripple documentation site

The Ripple docs, built with [Docusaurus 3](https://docusaurus.io/) (docs-only mode, served at `/`) and
the [Cosmos](https://sckyzo.github.io/cosmos-docusaurus-theme) theme, with Mermaid diagrams and KaTeX math.
Published to <https://ripple.x86.cc/>.

Adapted from the `x86cc.Documentation` template.

## Run locally

```bash
cd docs
mise install   # Node 24, pinned in mise.toml (any Node 18+ works)
npm ci
npm start      # http://localhost:3000/ with hot reload
```

```bash
npm run build  # production build -> build/ — fails on any broken internal link
npm run serve  # serve the production build
npm run clear  # clear the Docusaurus cache if nav/config changes don't show up
```

Or build without Node, exactly as CI does:

```bash
docker build -t x86cc-rippleengine-docs docs
```

## Layout

- `content/` — the Markdown pages (named `content/`, not `docs/`, to avoid `docs/docs/`).
- `sidebars.ts` — the sidebar is **curated by hand**: a new page under `content/` doesn't appear in the nav
  until it is listed here.
- `docusaurus.config.ts` — site config: Pages `url`/`baseUrl`, Mermaid, KaTeX, theme.
- `static/img/` — logo / favicon.

Diagrams are ` ```mermaid ` fences; math is `$…$` / `$$…$$`.

## Deployment

`.github/workflows/docs.yml` (repo root) builds `docs/Dockerfile` on every push to `main` that touches
`docs/**` (or manually) and publishes it with `actions/upload-pages-artifact` + `actions/deploy-pages` — no
`gh-pages` branch. Pull requests build only. **One-time setup:** in the repo's **Settings → Pages**, set
**Source** to **GitHub Actions**.
