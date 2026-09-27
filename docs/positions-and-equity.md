# Positions, trades and equity

A `hypothesis` run reports that a condition held and what the price did next. That measures the
market. It cannot say whether *trading* the signal made money, because it never holds anything: no
position, no entry or exit price, no fee, and no equity.

The `position` experiment is the part that answers the trader's question. It opens a position when
the signal fires, holds it against a rule, marks it every observation, and reports the account.

```json
{
  "experimentName": "position",
  "experimentConfig": {
    "entry_condition": "trade_flow < 0",
    "exit_condition": "trade_flow >= 0",
    "holding_observations": "5",
    "direction": "long",
    "position_fraction": "0.5",
    "starting_cash": "100000",
    "fee_bps": "5"
  }
}
```

## Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `entry_condition` | required | Condition that opens a position, same syntax as `hypothesis` |
| `exit_condition` | none | Condition that closes it. When omitted, the time rule applies |
| `holding_observations` | `20` | Observations to hold before a time exit |
| `min_hold_observations` | `1` | Observations to hold regardless, so one-tick noise does not trade |
| `direction` | `long` | `long` or `short` |
| `position_fraction` | `0.5` | Fraction of equity committed per position |
| `starting_cash` | `100000` | Account size |
| `fee_bps` | `0` | Fee per fill, in basis points of notional |
| `price_field` | `close` | Execution price: `close`, `last`, `mid`, `bid` or `ask` |

An unknown `direction` or `price_field` is rejected rather than defaulted. A misspelled price field
would otherwise produce a confident equity curve measured at the wrong price.

## Outputs

Four artifacts under `experiment/`:

| File | Contents |
| --- | --- |
| `position.csv` | Per-observation position state |
| `position_trades.csv` | One row per completed round trip: entry and exit time and price, quantity, fees, P&L |
| `position_portfolio.csv` | The marked equity curve: cash, position value, equity, drawdown |
| `position_fills.csv` | Every executed leg with its reason (open, increase, reduce, close, reverse) |

`position_trades.csv` is the trade log. Each row is a real round trip: a filled entry, a position
held, a filled exit, and the net result including every fee.

## The accounting invariant

Every output obeys:

```
ending_equity - starting_cash == realized_pnl + unrealized_pnl
```

and every completed trade's P&L sums to that same change. This holds for any sequence of opens,
adds, trims and reversals, not just clean open-then-close cases, and it is enforced by unit tests
against randomly generated fill sequences.

The invariant is what makes the trade log and the equity curve trustworthy together. If they ever
disagree, one of them is wrong, and a plausible-looking number built on a broken identity is worse
than an obvious failure.

Fees are a realized cost the moment they are charged, on entry as well as on exit. Attributing them
to the position instead would leave the account carrying a cost that appears in no trade and no
P&L figure, and the identity would fail by exactly the entry fees.

A position still open when the data ends is liquidated at the last observed price, so a result does
not silently depend on where the run was truncated.

## What is not modelled

Intrabar fills, slippage, borrow costs, margin calls, partial-fill probability and funding. They
are real and they matter. They are also a separate concern from whether the engine can hold a
position and account for it honestly, so they are named here rather than approximated.

Expect a naive strategy to lose exactly its fees. A signal that flips faster than it can be traded
pays the spread twice per round trip and nothing else, which is the correct answer, not a bug.

## Reading the result

```bash
python3 scripts/quantlab.py run --job job.json --data-dir FEEDS --output-dir OUT
```

A run that traded prints an account summary: starting cash, ending equity, return, peak, max
drawdown, realized and unrealized P&L, fees, trade count, win rate, and the first few trades with
their entry and exit prices.

## Tests

- `Tests/Research/EngineTests/PaperPortfolioTests.cs` — fills, sides, partials, reversals, fees,
  drawdown, sizing, and the accounting identity against random fill sequences
- `Tests/Research/EngineTests/PositionExperimentTests.cs` — entry and exit, long and short, fees,
  end-of-run liquidation, published tables and metrics, and rejection of bad configuration
