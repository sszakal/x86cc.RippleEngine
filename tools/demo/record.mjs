// Records the RippleEngine sample overview demo: waves triggered from Swagger, then watched and steered
// (reschedule, pause, resume) from the dashboard.
// Usage: node record.mjs <outDir>   (WebAPI on :5100, a worker's dashboard on :5200; see README.md)
// Frames come from Chrome's screencast (crisp, 1.5x); build.sh turns them into an mp4.
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const API = process.env.API ?? 'http://localhost:5100';
const DASH = process.env.DASH ?? 'http://localhost:5200';
const CORP = { code: process.env.CORP_CODE ?? 'TAX-200K', rate: '0.25' };
const EMP = { code: process.env.EMP_CODE ?? 'TAX-100K', rate: '14.5' };
const CORP_KEY = 'CorporateTaxChange|RecalcCorporateTax';
const EMP_KEY = 'EmployeeTaxChange|RecalcEmployeeTax';
const out = process.argv[2] ?? 'out';
const framesDir = path.join(out, 'frames');
fs.rmSync(framesDir, { recursive: true, force: true }); // frames of an earlier run would linger
fs.mkdirSync(framesDir, { recursive: true });

// ── page helpers injected into every document: a visible cursor, captions and title cards ──
const helpers = () => {
  const ready = () => {
    if (document.getElementById('__demo-css')) return;
    const css = document.createElement('style');
    css.id = '__demo-css';
    css.textContent = `
      #__cursor{position:fixed;z-index:2147483647;left:0;top:0;width:22px;height:22px;pointer-events:none;
        transform:translate(-3px,-2px);transition:transform .05s}
      #__cursor svg{filter:drop-shadow(0 2px 3px rgba(0,0,0,.35))}
      .__ripple{position:fixed;z-index:2147483646;width:34px;height:34px;margin:-17px 0 0 -17px;border-radius:50%;
        border:3px solid #465fff;pointer-events:none;animation:__rip .5s ease-out forwards}
      @keyframes __rip{from{transform:scale(.3);opacity:1}to{transform:scale(1.4);opacity:0}}
      #__caption{position:fixed;z-index:2147483645;left:50%;bottom:48px;transform:translate(-50%,12px);opacity:0;
        transition:opacity .35s,transform .35s;pointer-events:none;max-width:900px;text-align:center;
        background:rgba(17,24,39,.94);color:#fff;border:1px solid rgba(117,146,255,.45);border-radius:14px;padding:12px 22px;
        font:600 17px/1.35 Outfit,system-ui,sans-serif;box-shadow:0 10px 30px rgba(0,0,0,.25)}
      #__caption.on{opacity:1;transform:translate(-50%,0)}
      #__caption.side{left:auto;right:26px;bottom:auto;top:50%;max-width:330px;text-align:left;transform:translate(12px,-50%)}
      #__caption.side.on{transform:translate(0,-50%)}
      #__caption small{display:block;margin-top:3px;font-weight:400;font-size:14px;color:#c7d2fe}
      #__card{position:fixed;z-index:2147483646;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;
        background:radial-gradient(1200px 600px at 50% 40%,#2a3a9e 0%,#111827 70%);color:#fff;opacity:0;transition:opacity .5s;
        font-family:Outfit,system-ui,sans-serif;pointer-events:none}
      #__card.on{opacity:1}
      #__card circle.r{transform-origin:32px 32px;animation:__wave 2.4s ease-out infinite}
      #__card circle.r2{animation-delay:.8s}
      #__card circle.r3{animation-delay:1.6s}
      @keyframes __wave{from{transform:scale(.35);opacity:.9}to{transform:scale(1);opacity:0}}
      #__card h1{font-size:64px;font-weight:700;margin:22px 0 8px;letter-spacing:-.5px}
      #__card p{font-size:24px;color:#c7d2fe;margin:0;max-width:900px;text-align:center}
      #__card .u{margin-top:30px;font-size:20px;color:#7592ff;font-weight:600}
      #__ff{position:fixed;z-index:2147483645;left:50%;top:18px;transform:translateX(-50%);opacity:0;transition:opacity .3s;pointer-events:none;
        background:#465fff;color:#fff;border-radius:999px;padding:6px 14px;font:600 14px Outfit,system-ui,sans-serif}
      #__ff.on{opacity:1}`;
    document.head.appendChild(css);
    const c = document.createElement('div');
    c.id = '__cursor';
    c.innerHTML = `<svg width="22" height="22" viewBox="0 0 24 24"><path d="M3 2l7.5 19 2.6-7.9L21 10.5z" fill="#fff" stroke="#111827" stroke-width="1.6" stroke-linejoin="round"/></svg>`;
    const pos = window.__cursorPos ?? { x: -50, y: -50 };
    c.style.left = pos.x + 'px'; c.style.top = pos.y + 'px';
    document.body.appendChild(c);
  };
  document.addEventListener('mousemove', (e) => {
    window.__cursorPos = { x: e.clientX, y: e.clientY };
    const c = document.getElementById('__cursor');
    if (c) { c.style.left = e.clientX + 'px'; c.style.top = e.clientY + 'px'; }
  }, true);
  document.addEventListener('mousedown', (e) => {
    const r = document.createElement('div'); r.className = '__ripple';
    r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px';
    document.body.appendChild(r); setTimeout(() => r.remove(), 600);
  }, true);
  window.__caption = (text, sub, side) => {
    ready();
    let el = document.getElementById('__caption');
    if (!el) { el = document.createElement('div'); el.id = '__caption'; document.body.appendChild(el); }
    if (!text) { el.classList.remove('on'); return; }
    el.innerHTML = text + (sub ? `<small>${sub}</small>` : '');
    el.classList.toggle('side', !!side);
    requestAnimationFrame(() => el.classList.add('on'));
  };
  window.__ff = (text) => {
    ready();
    let el = document.getElementById('__ff');
    if (!el) { el = document.createElement('div'); el.id = '__ff'; document.body.appendChild(el); }
    if (!text) { el.classList.remove('on'); return; }
    el.textContent = text;
    requestAnimationFrame(() => el.classList.add('on'));
  };
  window.__card = (title, sub, url) => {
    ready();
    let el = document.getElementById('__card');
    if (!el) { el = document.createElement('div'); el.id = '__card'; document.body.appendChild(el); }
    const cur = document.getElementById('__cursor');
    if (cur) cur.style.visibility = title ? 'hidden' : 'visible';
    if (!title) { el.classList.remove('on'); return; }
    el.innerHTML = `<svg width="110" height="110" viewBox="0 0 64 64" fill="none">
        <circle class="r" cx="32" cy="32" r="30" stroke="#7592ff" stroke-width="2.5"/>
        <circle class="r r2" cx="32" cy="32" r="30" stroke="#7592ff" stroke-width="2.5"/>
        <circle class="r r3" cx="32" cy="32" r="30" stroke="#7592ff" stroke-width="2.5"/>
        <circle cx="32" cy="32" r="8" fill="#465fff"/></svg>
      <h1>${title}</h1><p>${sub ?? ''}</p>${url ? `<div class="u">${url}</div>` : ''}`;
    requestAnimationFrame(() => el.classList.add('on'));
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ready); else ready();
};

// ── the sample API (setup + polling; the recorded calls go through Swagger) ──
async function api(method, url) {
  const res = await fetch(`${API}${url}`, { method });
  if (!res.ok) throw new Error(`${method} ${url} → ${res.status} ${await res.text()}`);
  return res.json();
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const waveByName = async (name) => (await api('GET', '/waves')).find((w) => w.name === name);

// The scenario needs companies under both tax codes. Seed them (off camera) if the database is empty.
const codes = await api('GET', '/tax-codes');
if ([CORP.code, EMP.code].some((c) => (codes.find((x) => x.code === c)?.actual ?? 0) === 0)) {
  // The plan fills the codes in ascending order, so seed up to and including the larger of the two (311,000 by default).
  const last = Math.max(...[CORP.code, EMP.code].map((c) => codes.findIndex((x) => x.code === c)));
  const total = codes.slice(0, last + 1).reduce((a, c) => a + c.expected, 0);
  console.log(`seeding ${total.toLocaleString()} companies…`);
  const { waveId } = await api('POST', `/seed?total=${total}&batchSize=5000`);
  while ((await api('GET', `/waves/${waveId}`)).status === 'Active') await sleep(2000);
  // A fresh bulk load leaves Postgres flushing WAL and autovacuuming for a while, which can stall the workers'
  // claims past their deadline. Let it settle so the recorded waves start at full speed.
  await sleep(20000);
}
// Make sure neither type is still parked from an earlier, interrupted run.
for (const key of [CORP_KEY, EMP_KEY]) {
  await fetch(`${DASH}/api/settings/types/${encodeURIComponent(key)}/resume?rebase=true`, { method: 'POST' }).catch(() => {});
}

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM ?? '/usr/bin/chromium', args: ['--no-sandbox', '--hide-scrollbars'] });
const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1.5 });
await context.addInitScript(helpers);
// The dashboard's own dark theme. Swagger UI has none, so its content is inverted (hue-rotated back, so the
// method colours keep their hue) and the parts that were already dark or coloured (code blocks, top bar, method badges) are inverted back; the overlays live outside
// #swagger-ui and stay as they are.
await context.addInitScript(() => {
  try { localStorage.setItem('ripple.theme', 'dark'); } catch { /* ignore */ }
  if (!location.pathname.startsWith('/swagger')) return;
  const css = document.createElement('style');
  css.textContent = `html,body{background:#0f1523!important}
    #swagger-ui{filter:invert(.9) hue-rotate(180deg)}
    #swagger-ui pre,#swagger-ui img,#swagger-ui .topbar,#swagger-ui .opblock-summary-method,#swagger-ui .btn.execute{filter:invert(1) hue-rotate(180deg)}`;
  document.addEventListener('DOMContentLoaded', () => document.head.appendChild(css));
});
const p = await context.newPage();
p.setDefaultTimeout(15000);

// ── screencast capture; a fast-forward stops the cast and its gap is cut out of the timeline ──
const frames = [];
let cut = 0;
const cdp = await context.newCDPSession(p);
cdp.on('Page.screencastFrame', async ({ data, metadata, sessionId }) => {
  const file = path.join(framesDir, `f${String(frames.length).padStart(6, '0')}.jpg`);
  fs.writeFileSync(file, Buffer.from(data, 'base64'));
  frames.push({ file: path.basename(file), t: metadata.timestamp - cut });
  await cdp.send('Page.screencastFrameAck', { sessionId }).catch(() => {});
});
const startCast = () => cdp.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: 1920, maxHeight: 1200, everyNthFrame: 1 });
const stopCast = () => cdp.send('Page.stopScreencast');

// ── interaction helpers ──
const PACE = Number(process.env.PACE ?? 0.85);
const wait = (ms) => p.waitForTimeout(Math.round(ms * PACE));
// Captions sit at the bottom; `side` puts one to the right of the centred wave popup so it never covers it.
const caption = async (text, sub, side = false) => { await p.evaluate(([t, s, sd]) => window.__caption(t, s, sd), [text, sub, side]); };
const card = async (title, sub, url) => { await p.evaluate(([t, s, u]) => window.__card(t, s, u), [title, sub, url]); };
let mouse = { x: 640, y: 400 };
const moveTo = async (loc, dx = 0.5, dy = 0.5) => {
  await loc.scrollIntoViewIfNeeded();
  const box = await loc.boundingBox();
  const x = box.x + box.width * dx, y = box.y + box.height * dy;
  const dist = Math.hypot(x - mouse.x, y - mouse.y);
  await p.mouse.move(x, y, { steps: Math.max(8, Math.min(40, Math.round(dist / 18))) });
  mouse = { x, y };
  await wait(120);
};
const click = async (loc, pause = 500) => { await moveTo(loc); await p.mouse.down(); await p.mouse.up(); await wait(pause); };
const type = async (loc, text, pause = 300) => {
  await click(loc, 150);
  await loc.press('Control+a');
  await loc.pressSequentially(text, { delay: 35 });
  await wait(pause);
};
const scrollTo = async (y) => { await p.evaluate((y) => window.scrollTo({ top: y, behavior: 'smooth' }), y); await wait(900); };
const settle = async () => { await p.waitForLoadState('networkidle').catch(() => {}); await p.evaluate(() => document.fonts.ready); };
const go = async (url) => { await p.goto(url); await settle(); };

/** Cuts the wait for `done()` out of the video, with a fast-forward badge either side of the cut. */
const fastForward = async (label, done) => {
  await p.evaluate((t) => window.__ff(t), `⏩ ${label}`);
  await wait(1200);
  await stopCast();
  const from = Date.now() / 1000;
  while (!(await done())) await sleep(1000);
  cut += Date.now() / 1000 - from;
  await startCast();
};
const ffOff = () => p.evaluate(() => window.__ff(null));

// ── swagger helpers ──
const op = (method, route) => p.locator(`.opblock-${method}`, { has: p.locator(`.opblock-summary-path[data-path="${route}"]`) }).first();
const expand = (o) => click(o.locator('.opblock-summary').first(), 700);
const tryOut = (o) => click(o.locator('button.try-out__btn'), 500);
const param = (o, name) => o.locator(`tr[data-param-name="${name}"] input`).first();
const execute = async (o) => {
  await click(o.locator('button.execute'), 300);
  await o.locator('.live-responses-table .response-col_description pre').first().waitFor();
  await wait(400);
  await moveTo(o.locator('.live-responses-table .response-col_description pre').first(), 0.3, 0.5);
};

// ── dashboard helpers ──
const now = new Date();
const [Y, M, D, H] = [now.getFullYear(), now.getMonth() + 1, now.getDate(), now.getHours()];
const hourUrl = `${DASH}/waves/${Y}/${M}/${D}/${H}`;
const corpName = `Corporate tax ${CORP.code}`;
const empName = `Employee tax ${EMP.code}`;
const timelineItem = (name) => p.locator('app-wave-timeline button', { hasText: name }).first();
const popup = () => p.locator('app-wave-popup');
const typeRow = (key) => p.locator('tr', { has: p.locator('div.font-mono', { hasText: key }) }).first();
const rowBtn = (key, text) => typeRow(key).getByRole('button', { name: text, exact: true });

try {
  await go(`${API}/swagger/index.html`);
  await p.locator('.opblock').first().waitFor();
  await card('RippleEngine', 'Massive, set-based fan-out over Postgres: one business event, millions of tasks');
  await wait(400);
  await startCast();

  // ── 1. Title ──
  await wait(3200);
  await card(null);
  await wait(500);
  await caption('The sample: a company / taxation API', 'A Wave is one business event; a Ripple is one task per affected company');
  await wait(3000);

  // ── 2. What's in the database ──
  const taxCodes = op('get', '/tax-codes');
  await expand(taxCodes);
  await tryOut(taxCodes);
  await execute(taxCodes);
  await caption('311,000 companies, grouped by tax code', 'Seeded by an earlier wave whose handler bulk-inserts the companies');
  await wait(3200);
  await expand(taxCodes);
  await caption(null);

  // ── 3. Trigger the corporate-tax wave ──
  const corp = op('post', '/corporate-tax');
  await expand(corp);
  await caption(`Corporate tax for ${CORP.code} changes…`, 'One HTTP call creates one wave');
  await tryOut(corp);
  await type(param(corp, 'taxCode'), CORP.code);
  await type(param(corp, 'rate'), CORP.rate, 400);
  await execute(corp);
  await caption('…and 200,000 ripples are created with one INSERT … SELECT',
    'The companies never leave Postgres; the API returns as soon as the rows are written');
  await wait(4200);
  await expand(corp);

  // ── 4. And a competing employee-tax wave ──
  const emp = op('post', '/employee-tax');
  await expand(emp);
  await caption(`A second wave: employee tax for ${EMP.code}`, 'A different job type, so the two waves share the cluster');
  await tryOut(emp);
  await type(param(emp, 'taxCode'), EMP.code);
  await type(param(emp, 'ratePerEmployee'), EMP.rate, 400);
  await execute(emp);
  await wait(2800);
  await caption(null);

  // ── 5. The dashboard: the cluster ──
  await go(`${DASH}/metrics`);
  await caption('The dashboard runs inside every worker', 'Three workers poll the database and claim ripples with FOR UPDATE SKIP LOCKED');
  await wait(2500);
  await go(`${DASH}/metrics`); // the cluster card is a snapshot; reload it to show the slots filling
  await caption('Each job type has a scheduling share (batch ÷ gap)',
    'Employee tax (15/s) gets about 3 slots for every 1 that corporate tax (5/s) gets. Waves interleave instead of queueing');
  await moveTo(p.locator('text=Schedule share').first());
  await wait(4200);
  await caption(null);

  // ── 6. Drill down to the waves ──
  await click(p.locator('nav a[href="/waves"], a[href="/waves"]').first(), 900);
  await caption('Waves, by year, month, day and hour');
  await click(p.locator(`a[href="/waves/${Y}"]`).last(), 900);
  await click(p.locator(`a[href="/waves/${Y}/${M}"]`).last(), 900);
  await click(p.locator(`a[href="/waves/${Y}/${M}/${D}"]`).last(), 900);
  await click(p.locator(`a[href="/waves/${Y}/${M}/${D}/${H}"]`).last(), 1000);
  await settle();
  await caption(null);

  // ── 7. Live progress ──
  await click(timelineItem(corpName), 900);
  await click(popup().getByRole('button', { name: '2s', exact: true }), 300);
  await caption('Live progress for one wave', 'Refreshed from the ripple rows, not from counters on the hot path', true);
  await wait(5000);

  // ── 8. Reschedule ──
  await caption('Reschedule: give the corporate-tax wave more of the cluster',
    'Its pending ripples are re-ordered at the wave\'s current position: 45 per second instead of 5', true);
  await click(popup().getByRole('button', { name: /Reschedule remaining ripples/ }), 500);
  const [batchIn, gapIn] = [popup().locator('input[type=number]').nth(0), popup().locator('input[type=number]').nth(1)];
  await type(batchIn, '45');
  await type(gapIn, '1', 500);
  await click(popup().getByRole('button', { name: 'Apply', exact: true }), 300);
  await popup().locator('text=re-stamped').waitFor();
  await moveTo(popup().locator('text=re-stamped'));
  await wait(3500);
  await caption('Corporate tax now gets 3× employee tax\'s share', 'Watch its progress speed up', true);
  await moveTo(popup().getByText('Progress', { exact: true }));
  await wait(5000);
  await click(popup().getByRole('button', { name: 'Close', exact: true }), 500);
  await caption(null);

  // ── 9. Pause ──
  await click(p.locator('a[href="/settings"]').first(), 900);
  await settle();
  await caption('Pause a job type', 'Its pending ripples are parked; ripples already running finish normally');
  await moveTo(typeRow(EMP_KEY).locator('div.font-mono'));
  await wait(1500);
  await click(rowBtn(EMP_KEY, 'Pause'), 1200);
  await typeRow(EMP_KEY).locator('text=Paused').waitFor();
  await moveTo(typeRow(EMP_KEY).locator('text=Paused'));
  await wait(2200);

  await go(`${hourUrl}?wave=${(await waveByName(empName)).id}`);
  await click(popup().getByRole('button', { name: '2s', exact: true }), 300);
  await caption('The employee-tax wave is on hold', 'Its parked ripples are counted as Paused, and pending and running drop to zero', true);
  await popup().locator('dt', { hasText: 'Paused' }).waitFor({ timeout: 20000 });
  await moveTo(popup().locator('dt', { hasText: 'Paused' }));
  await wait(4500);
  await click(popup().getByRole('button', { name: 'Close', exact: true }), 400);
  await click(timelineItem(corpName), 900);
  await click(popup().getByRole('button', { name: '2s', exact: true }), 300);
  await caption('Corporate tax gets the whole cluster in the meantime', null, true);
  await moveTo(popup().getByText('Progress', { exact: true }));
  await wait(4500);
  await click(popup().getByRole('button', { name: 'Close', exact: true }), 400);
  await caption(null);

  // ── 10. Resume ──
  await click(p.locator('a[href="/settings"]').first(), 900);
  await settle();
  await click(rowBtn(EMP_KEY, 'Resume'), 700);
  await caption('Resume from now, or as it was',
    '"From now" moves the parked ripples to the back of the current queue so they share fairly. "As-is" keeps their old place, so they run first');
  await moveTo(rowBtn(EMP_KEY, 'From now'));
  await wait(4200);
  await click(rowBtn(EMP_KEY, 'From now'), 1200);
  await rowBtn(EMP_KEY, 'Pause').waitFor({ timeout: 20000 });
  await caption(null);

  // ── 11. Both waves drain ──
  await go(hourUrl);
  await caption('Both waves run to completion');
  await wait(1500);
  await fastForward('fast-forward', async () => {
    const [c, e] = [await waveByName(corpName), await waveByName(empName)];
    return c?.status === 'Completed' && e?.status === 'Completed';
  });
  await go(hourUrl);
  await p.evaluate((t) => window.__ff(t), '⏩ fast-forward');
  await wait(1500);
  await ffOff();
  await caption('300,000 tasks, three workers, no message broker', 'Postgres is the only source of truth');
  await moveTo(timelineItem(corpName));
  await wait(3500);
  await click(timelineItem(corpName), 1500);
  await caption('Each finished wave keeps a compact report', 'Its ripple rows are rolled up and deleted, so the tables don\'t grow', true);
  await popup().getByRole('button', { name: /Download report/ }).waitFor({ timeout: 30000 }).catch(() => {});
  await wait(4200);
  await caption(null);
  await click(popup().getByRole('button', { name: 'Close', exact: true }), 400);

  // ── 12. End card ──
  await card('RippleEngine', 'Fan-out · fair scheduling · pause / resume · reschedule · recovery · dashboard');
  await wait(4000);
} catch (e) {
  await p.screenshot({ path: path.join(out, 'failure.png') });
  console.error(e);
  process.exitCode = 1;
} finally {
  await stopCast().catch(() => {});
  fs.writeFileSync(path.join(out, 'frames.json'), JSON.stringify({ frames, end: Date.now() / 1000 - cut }));
  await browser.close();
  console.log(`${frames.length} frames, ${Math.round(cut)}s cut`);
}
