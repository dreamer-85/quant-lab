# Validation guardrails

A research job can complete successfully while producing numbers that are
mathematically wrong. The failure is silent: the CSV has the right columns, the
row count looks plausible, and the hypothesis simply never fires. The most
valuable checks are therefore not "did the pipeline run" but "are these values
internally possible".

Validation runs alongside replay and answers that question. It is configured
per job, defaults to warning, and never changes a computed value.

## Enabling and configuring

Validation is configured through `experimentConfig`, so it needs no schema
change and does not affect `GetConfigurationHash()` — turning a warning into a
failure does not invalidate a checkpoint.

```json
"experimentConfig": {
  "validation.mode": "warn",
  "validation.max_findings": "20",
  "validation.checks": "config,market_state,observation,flow,numeric,degenerate,duplicate,grid,freshness,coverage"
}
```

| Key | Values | Default | Meaning |
| --- | ------ | ------- | ------- |
| `validation.mode` | `off`, `warn`, `fail` | `warn` | `warn` reports; `fail` aborts on any error-severity finding |
| `validation.max_findings` | integer | `20` | Distinct findings retained per check |
| `validation.checks` | comma-separated check ids | all | Restrict to a subset |

A finding is `Info`, `Warning` or `Error`. Only `Error` aborts under `fail`;
warnings are reported and the run completes, because a warning is often a real
property of the data rather than a mistake.

`off` writes no report at all, so the presence of
`<output>/<jobId>/validation_report.json` means "validation ran" and its absence
means "it did not".

## Checks

### `config` — job configuration intent (preflight, before any replay)

Catches the mistakes that otherwise produce a clean-looking empty or constant
result:

- a `features` or `rawFields` entry that does not exist, with the closest real
  name suggested. An unresolvable name aborts the run regardless of mode: the
  job cannot produce a meaningful result, so there is nothing to warn about.
- a `hypothesis` condition whose measurement is not a feature value — either an
  unselected registered feature (fix: add it to `job.features`) or a raw field
  such as `trade_count` (fix: pick a registered feature; raw fields cannot be
  used in conditions).
- a depth feature selected while `eventTypes` contains no
  `OrderBookUpdate`/`OrderBookSnapshot`. Those features return `0` for every
  observation, so the condition never fires.
- an unrecognised `feature.<name>.<param>` key, which is silently ignored by the
  feature and leaves the default in the output column name.
- a raw field and a feature sharing a name. The feature value wins, so the raw
  value never reaches the output.
- `trade_flow` and `net_flow` selected together: they are the same quantity, so
  the output gains an identical column.
- a `observationInterval` that implies an unreasonable number of rows for the
  requested window. With `fillForward` on, the row count follows the clock
  rather than the data, so this is the only cost signal available before a single
  event is read. It deliberately says nothing about whether the cadence matches
  the data: `resolution` is the bar resolution the job wants, not the cadence of
  the events it replays, so inferring one from the other would warn on valid
  tick-level jobs. The real ratio is measured by `freshness` during the run.

### `grid` — observation clock integrity (per observation)

The invariants of the observation clock itself. A violation here means the
scheduler is wrong, not that the market behaved oddly, and every feature value in
the run is suspect because they are all measured against this clock:

- an observation stamped **earlier than the newest event it contains**. This is
  the check that would have caught the original sparse-data defect, where a
  period stamped `00:01` carried the `00:10` trade.
- timestamps that do not advance, so period-dependent features are undefined.
- an irregular step, meaning a period was dropped and any rolling window now
  spans an unequal amount of time.
- a newest-event timestamp that moves backwards, meaning the source delivered
  events out of order.

### `freshness` — how much of the output is real (run-level)

Reads the published `data_quality` and `data_age_ms` columns and reports the
ratio of periods that carried new data, so a run whose periods are mostly padding
is visible rather than inferred from the row count:

- a padding ratio above 50%, with the counts of fresh, filled and missing
  periods. Padding past that point usually means `observationInterval` is finer
  than the data, and a fixed-period window is covering far more wall-clock time
  than its period count suggests.
- any period that was `missing` (no data *and* no prior state to carry forward),
  which means the stream began after `startTime`.
- the oldest observed `data_age_ms`, so a run carrying forward state that is
  seconds or minutes stale is reported in those terms.

### `coverage` — did the run span the window it was asked for (run-level)

A run that silently covers a fraction of the requested window still produces a
complete-looking output file, so coverage is compared against the job's own
bounds rather than inferred from the output:

- an empty output file, or coverage that runs backwards.
- observations starting after the requested `startTime`, so features that need
  history to warm up start colder than expected.
- a **truncated** run — one that stopped because it hit `maxObservations`,
  `maxEvents` or the requested end rather than because the data ended. Reported
  as a valid prefix, so a cut-short file is never mistaken for a complete window.
- a tail that stops short of the requested end *while `fillForward` is on*, which
  the padding guarantees should have reached it. With `fillForward` off the
  series is irregular by design and ends where the data ends, so this is not
  reported.
- late events, meaning events arrived after the period they belong to was already
  emitted. Under `InOrderStreaming`, which trusts its input, this is a
  source-contract violation rather than a scheduling choice.

### `market_state` — reconstructed book consistency (per observation)

A violation means state reconstruction is wrong, not that the market moved oddly:

- crossed book (`bid > ask`) or non-positive prices
- an internal `mid`/`spread` inconsistent with bid and ask
- non-finite or negative depth/size values
- `imbalance` outside `[-1, 1]`, or inconsistent with bid/ask depth
- depth that does not match the sum of its levels

### `observation` — OHLC/vwap coherence (per observation)

- inverted or empty traded range
- `vwap` outside the traded range
- an `open` or `close` outside the range, **only when that endpoint came from a
  trade**. `OpenPrice`/`ClosePrice` read a quote mid when the boundary event is a
  quote, while `HighPrice`/`LowPrice` are trade-only; the two series differ by a
  tick, so a quote-derived endpoint is not range-checked.
- `open = 0` while trades occurred. `OpenPrice` only reads the first event when it
  is a trade or quote, so a period opening with a book update yields `0` rather
  than a price. This is a warning because it is a real property of many jobs, but
  it will silently corrupt any strategy that treats `open` as a price.

### `flow` — identities that must hold (per observation)

- `aggressive_buy_volume + aggressive_sell_volume == trade_flow == net_flow`.
  A violation means a flow feature regressed to an unsigned or partial measure —
  for example a `trade_flow < 0` threshold that stops firing because buys and
  sells were summed instead of subtracted.

### `numeric` — range and finiteness (per observation)

- non-finite values
- a negative value for a measure documented as non-negative (depths, volumes,
  ratios, `imbalance`, `liquidity_wall`, `resistance`). Parameters that can
  legitimately be negative, such as `liquidity_depletion` (a *change* in depth)
  and `cumulative_flow` (a running signed sum), are excluded by matching the
  measurement's stem.

### `degenerate` — columns that carry no signal (run-level)

A column that is constant across every observation, reported with its
constant value. A column of zeros usually means the job never received the data
it asked for; a constant non-zero value usually means a feature is not
computing what its name claims.

### `duplicate` — columns that are secretly the same (run-level)

Two or more columns with identical values across all observations. This is how a
flow feature that silently duplicated another one gets noticed.

## Output

`validation_report.json` is written next to the run outputs:

```json
{
  "jobId": "hypothesis-imbalance-demo",
  "configurationHash": "c5ef6f50...",
  "mode": "Warn",
  "checks": ["config", "market_state", "..."],
  "observationsChecked": 61,
  "raisedCounts": { "observation": 59 },
  "findings": [
    {
      "Check": "observation",
      "severity": "Warning",
      "Message": "Period with 1 trade(s) reports open = 0. ...",
      "Symbol": "BTCUSDT",
      "Occurrences": 59,
      "firstTimestamp": "2024-01-01T00:01:00.0000000",
      "lastTimestamp": "2024-01-01T00:59:00.0000000"
    }
  ]
}
```

### Repeated findings are aggregated

A wrong formula applied to every row is one problem, not 60. Findings with the
same check, symbol and message are merged into a single entry with
`Occurrences` and a first/last timestamp range, so the report stays readable and
the reader can see the blast radius. `RaisedCounts` reports the true total per
check even when individual findings were suppressed by `max_findings`.

A guardrail that repeats itself on every row trains people to ignore it, which
is worse than having no guardrail at all.

## Bounded memory

Validation must not undo the streaming guarantees in `bounded-memory.md`:

- preflight checks are per job and constant;
- per-observation findings are aggregated into a bounded set of distinct
  problems, so memory is a function of the number of distinct problems, not of
  dataset size;
- `degenerate` keeps one counter per numeric column, and `duplicate` keeps one
  hash per column — both bounded by the column count, which is fixed by the job.

## Distinguishing plumbing bugs from research results

The split to keep in mind when reading a finding:

- **`config` and `degenerate` findings are plumbing.** The job did not ask for
  what you are reading, or it never received the data. A condition that never
  fires because `imbalance` was `0` for 61 observations has told you nothing
  about the market.
- `market_state`, `observation` and `flow` findings are correctness bugs in the
  engine or in a feature's math. They should be treated as defects.
- `numeric` and `duplicate` findings are judgement calls: they are patterns that
  often indicate a mistake, but a genuinely constant series is possible.

In practice a plumbing mistake shows up in several checks at once. A job with
`imbalance` selected and no book events reports the `config` depth-dependency
error *and* two `degenerate` constant-zero errors, all naming the same root
cause.
