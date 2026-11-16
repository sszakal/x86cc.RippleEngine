---
title: Known limits
sidebar_position: 4
---

# Known limits & roadmap

Ripple is **pre-1.0** and not yet run in production. Each limit below is a deliberate trade, not an
omission.

- **The scheduler is static.** Once `schedule_order` is stamped it is never rebalanced, so thousands of jobs
  arriving at once can each sit one batch ahead of a newcomer — still far better than strict FIFO. The one
  exception is the operator-driven [wave reschedule](/architecture/scheduler#rescheduling-a-live-wave); it runs
  as one transaction, so a chunked, desired-state variant is the next phase for multi-million-ripple waves.
- **Wave numbers are eventually consistent** — up to one `WaveStatsRefreshInterval` stale, the trade for zero
  counter maintenance on the hot paths.
- **The stats refresh re-counts pending every tick.** One very large wave keeps re-counting its whole backlog;
  making that cheap at 10M would need an incremental design.
- **No per-type concurrency ceiling.** Global concurrency is `MaxConcurrency × instances`; a hard cap for a
  rate-limited dependency would need to be added.
- **Compaction, not partitioning.** The hot tables stay small via per-wave compaction; a very large wave's
  compaction `DELETE` is the one heavy statement. `created_at` RANGE partitioning is the escape hatch.
- **Index bloat.** `schedule_order` inserts land mid-index (interleaved by design), so
  `ix_ripple_schedule_order` bloats more than a monotonic index would — fine at POC scale.
- **Recovery is heartbeat/ownership based**; a pure time-based reaper is a later refinement.
- **Mixed clocks.** Backoff uses the host clock, eligibility the DB clock — fine when NTP-synced.
- **One migration.** The schema is a single authoritative migration on an ephemeral database, not an ALTER
  trail — freeze it and add forward migrations before running against a database you intend to keep.
- **Not transactional with your business change.** Wave creation runs on the engine's connection; use
  [`WithIdempotencyKey`](/guides/fan-out#idempotent-creation) to make retries safe.
