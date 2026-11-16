---
title: The scheduler
sidebar_position: 4
---

# The batch-interleaving scheduler

Fair-share is decided **once, at fan-out**, by stamping each ripple with a `schedule_order` — a
`double precision` **ordering key**, epoch-seconds based but treated as an opaque number, not a deadline.
Workers stay dumb: they claim the globally lowest `schedule_order` first. Nothing is ever rescheduled
automatically. This is static weighted-fair-queueing — a stride scheduler with stride `gap / batch_size`.

## The type key

The composite **`type_key = "{waveType}|{rippleType}"`** (e.g. `CorporateTaxChange|RecalcCorporateTax`) is both:

- the **config key** — `type_schedule(batch_size, gap_seconds, max_attempts)` is keyed by it, seeded by
  `AddHandler<TWave,TRipple,THandler>(batchSize, gapSeconds, maxAttempts)` at startup;
- the **handler key** — `RippleHandlerRegistry` resolves `IRippleHandler<TWave,TRipple>` by it.

## Stamping

For the ripple at position $k$ within its type in one fan-out:

$$
\text{schedule\_order}_k = \text{base} + \left\lfloor \frac{k}{\text{batch\_size}} \right\rfloor \cdot \text{gap\_seconds}
$$

$$
\text{base} = \operatorname{coalesce}\big(\text{tail},\ \text{frontier}\big)
$$

where $\text{tail}$ is the highest `schedule_order` among **this wave's** pending ripples (null for a new or
drained job), and

$$
\text{frontier} = \max\Big(\text{epoch}(\text{now}()),\ \min_{\text{all pending}} \text{schedule\_order},\ \max_{\text{all running}} \text{schedule\_order}\Big)
$$

- `batch_size` consecutive ripples share one slot; each slot is `gap_seconds` after the last.
- **Continuation appends.** New work for a job with pending ripples bases off that pending tail, so it queues
  *after* it (FIFO within a job).
- **A new or drained job starts at the global frontier**, not wall-clock `now()`.
- `base` and `now()` are **DB-clock**, so a lagging host can't jump the global queue.

### Why the frontier, not `now()`

The engine drains **far faster than one slot per `gap`**, so virtual time races ahead of the wall clock and an
in-flight job's frontier sits well in the *virtual future*. A late job based at bare `now()` would land far
*behind* that frontier, and the claim would drain its whole backlog exclusively — monopolising the cluster to
"catch up". Basing at the frontier makes it interleave **immediately** (the classic WFQ virtual-start rule).

The frontier also reads the **highest key currently `Running`**: a ripple waiting out a retry backoff, or one
of a type no live instance handles, keeps its low position and would otherwise pin the minimum back to `now()`.

Job A is draining a large backlog; job B arrives while the frontier is at slot 2 (equal batch/gap):

| `schedule_order` slot | 0 | 1 | 2 | 3 | 4 | … |
|---|---|---|---|---|---|---|
| **Job A** | A₁ | A₂ | A₃ | A₄ | A₅ | … |
| **Job B** | | | B₁ | B₂ | B₃ | … |

B's first batch is based at the frontier (slot 2), so from there each slot holds one batch of each job and the
claim order interleaves `… A₃ B₁ A₄ B₂ …` instead of draining A's whole backlog first — far better than strict
FIFO, where B would wait behind *every* earlier task.

## The fairness knob

A job's steady-state share of the schedule is

$$
\text{share}_j \approx \frac{\text{batch\_size}_j / \text{gap\_seconds}_j}{\sum_i \text{batch\_size}_i / \text{gap\_seconds}_i}
$$

Smaller batches / larger gaps spread a job more evenly so competitors progress sooner; larger batches / smaller
gaps favour throughput. Keep `batch_size ≪ cluster execution capacity` for a *blended* mix; batches ≥ capacity
give coarse slot-at-a-time alternation. In the sample, corporate (`batch 5`) vs employee (`batch 15`) at equal
gap ⇒ employee gets ~3× the share.

:::note No per-type concurrency ceiling
Global concurrency is simply `MaxConcurrency × instances`. The ordering key controls *share*, not an absolute
ceiling — a hard cap for a rate-limited dependency would need to be added separately.
:::

## Rescheduling a live wave

The one deliberate exception to "never rescheduled" is the operator-driven **reschedule**
(`POST /api/waves/{id}/reschedule`): it re-stamps one live wave's remaining `Pending` ripples with a new
batch/gap, anchored at that wave's own head so it keeps its queue position and only its share changes. It is
`for update skip locked` (never blocks a claim) and advisory-locked against a concurrent reschedule. `Running`
ripples and later continuations are not covered.
