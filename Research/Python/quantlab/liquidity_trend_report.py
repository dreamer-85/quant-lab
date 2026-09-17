import argparse
import json
import os
from pathlib import Path

import pandas as pd
import numpy as np

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

HORIZONS = ["5m", "15m"]
HORIZON_SECONDS = {"5m": 300, "15m": 900}


def bps_scale(vals):
    med = np.nanmedian(np.abs(vals))
    if med > 1.0:
        return 1.0  # already basis points? values look like % or bps
    if med < 1e-3:
        return 1e4  # fraction -> bps
    return 1e2  # percent -> bps


def summarize_ret(df, col):
    d = df[col].dropna()
    if len(d) == 0:
        return {"n": 0, "mean_bps": np.nan, "median_bps": np.nan, "hit": np.nan}
    scale = bps_scale(d)
    return {
        "n": int(len(d)),
        "mean_bps": float(d.mean() * scale),
        "median_bps": float(d.median() * scale),
        "hit": float((d > 0).mean()),
    }


def build_baseline(obs, horizon_sec):
    df = obs.copy()
    df["ts"] = pd.to_datetime(df["timestamp"])
    df = df[df["ts"].dt.year >= 2026]
    df = df.set_index("ts")["mid_price"].astype(float)
    df = df[~df.index.duplicated(keep="first")].sort_index()
    # only measure forward moves where a 1s observation exists exactly horizon later
    fwd = (df.shift(-int(horizon_sec)) / df - 1.0)
    return fwd.dropna()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results_dir")
    ap.add_argument("out_dir")
    args = ap.parse_args()

    results = Path(args.results_dir)
    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)

    ev = pd.read_parquet(results / "experiment" / "liquidity_trend.parquet")
    obs = pd.read_parquet(results / "BTCUSDT" / "bybit.parquet")
    with open(results / "experiment" / "liquidity_trend_metrics.json") as f:
        metrics = json.load(f)

    order = ["Depletion", "Replenishment", "WallFormation", "Migration"]
    ev["type"] = pd.Categorical(ev["type"], categories=order, ordered=True)

    lines = []
    lines.append("# Liquidity-Transition vs Forward Trend — BTCUSDT spot, 2026-09-17 08:22-09:22 UTC")
    lines.append("")
    lines.append(f"Source: 60-min live Bybit L2-50 depth + trades archive, replayed at 1s observation grid.")
    lines.append(f"Observations: {metrics['observation_count']} | transition events: {metrics['event_count']} "
                 f"({metrics['event_rate_per_minute']:.1f}/min).")
    lines.append("")
    lines.append("## 1. Transition inventory")
    lines.append("")
    lines.append("| Event | Count | Bid | Ask | Executed % | Mean net flow (USDT) | Mean bps band |")
    lines.append("|---|---|---|---|---|---|---|")
    for t in order:
        g = ev[ev["type"] == t]
        if len(g) == 0:
            lines.append(f"| {t} | 0 | - | - | - | - | - |")
            continue
        ex = g["executed"].astype(bool).mean() * 100 if "executed" in g else np.nan
        nf = g["net_flow"].astype(float).mean()
        bb = g["bps_band"].astype(float).mean()
        side = g.groupby("side").size()
        bid = int(side.get("Bid", 0))
        ask = int(side.get("Ask", 0))
        lines.append(f"| {t} | {len(g)} | {bid} | {ask} | {ex:.1f}% | {nf:,.0f} | {bb:.2f} |")
    lines.append("")

    lines.append("## 2. Forward drift by event type vs baseline (resolved events only)")
    lines.append("")
    baseline = {h: build_baseline(obs, HORIZON_SECONDS[h]) for h in HORIZONS}
    tab = []
    for h in HORIZONS:
        bl = baseline[h].dropna()
        bl_scale = bps_scale(bl)
        tab.append(f"Baseline {h}")
        tab.append(f"{bl.mean() * bl_scale:.4f}")
        tab.append(f"{bl.median() * bl_scale:.4f}")
        tab.append(f"{(bl > 0).mean() * 100:.1f}%")

    def cfmt(x):
        return "&nbsp;" if np.isnan(x) else f"{x:.3f}"

    for h in HORIZONS:
        bl = baseline[h].dropna()
        lines.append(f"### Horizon {h} — mean/median forward return in bps, hit rate")
        lines.append("")
        lines.append("| Bucket | N | Mean (bps) | Median (bps) | Hit rate | Mean MFE | Mean MAE |")
        lines.append("|---|---|---|---|---|---|---|")
        scale = bps_scale(ev["ret_" + h])
        lines.append(
            f"| Baseline (all 1s obs) | {len(bl)} | {bl.mean() * bps_scale(bl):.4f} | "
            f"{bl.median() * bps_scale(bl):.4f} | {(bl > 0).mean() * 100:.1f}% | - | - |")
        for t in order:
            g = ev[(ev["type"] == t) & ev["resolved_" + h]].copy()
            if len(g) == 0:
                lines.append(f"| {t} | 0 | - | - | - | - | - |")
                continue
            r = g["ret_" + h].astype(float)
            s = bps_scale(r)
            lines.append(
                f"| {t} | {len(g)} | {r.mean() * s:.4f} | {r.median() * s:.4f} | "
                f"{(r > 0).mean() * 100:.1f}% | {g['mfe_' + h].astype(float).mean() * s:.3f} | "
                f"{g['mae_' + h].astype(float).mean() * s:.3f} |")
        lines.append("")

    lines.append("## 3. Replenishment latency (depletions only)")
    lines.append("")
    dep = ev[ev["type"] == "Depletion"].copy()
    dep["ms_until_replenishment"] = dep["ms_until_replenishment"].astype(float)
    dep_repl = dep[dep["ms_until_replenishment"] > 0]["ms_until_replenishment"]
    if len(dep_repl):
        lines.append(f"- Depletions that replenished within lookback window: "
                     f"{dep['replenished_within_lookback'].astype(bool).mean() * 100:.1f}% "
                     f"({int(dep['replenished_within_lookback'].astype(bool).sum())}/{len(dep)})")
        lines.append(f"- Median latency to replenishment: {dep_repl.median() / 1000:.2f}s")
        lines.append(f"- P75/P90 latency: {dep_repl.quantile(0.75) / 1000:.2f}s / {dep_repl.quantile(0.9) / 1000:.2f}s")
        lines.append(f"- Mean net flow while depleting: {dep['net_flow'].astype(float).mean():,.0f} USDT (buy/sell)")
    else:
        lines.append("- No positive replenishment latencies recorded.")
    lines.append("")

    lines.append("## 4. Depth impact and trade pressure")
    lines.append("")
    lines.append("| Event | Mean depth delta (BTC) | Mean trade count in window | Mean MS since last event |")
    lines.append("|---|---|---|---|")
    for t in order:
        g = ev[ev["type"] == t]
        if len(g) == 0:
            continue
        lines.append(
            f"| {t} | {g['depth_delta'].astype(float).mean():.4f} | "
            f"{g['trade_count'].astype(float).mean():.1f} | {g['ms_since_last_event'].astype(float).mean():,.0f} |")
    lines.append("")

    # --- Charts
    fig1, axes = plt.subplots(1, 2, figsize=(13, 5), sharey=False)
    for ai, h in enumerate(HORIZONS):
        bl = baseline[h].dropna()
        scale_ev = bps_scale(ev["ret_" + h])
        names = [t for t in order if len(ev[(ev["type"] == t) & ev["resolved_" + h]]) > 0]
        means = [(ev[(ev["type"] == t) & ev["resolved_" + h]]["ret_" + h].astype(float).mean()) * scale_ev for t in names]
        bl_mean = bl.mean() * scale_ev
        ax = axes[ai]
        colors = ["#c0392b", "#27ae60", "#8e44ad", "#f39c12"][: len(names)]
        ax.bar(names, means, color=colors, alpha=0.85)
        ax.axhline(bl_mean, color="black", ls="--", lw=1, label=f"baseline {bl_mean:.3f} bps")
        ax.set_title(f"{h} forward return (bps), mean")
        ax.legend()
    fig1.suptitle("Mean forward return conditioned on liquidity transition (resolved events)")
    fig1.tight_layout(rect=(0, 0, 1, 0.95))
    fig1.savefig(out / "forward_drift_by_type.png", dpi=140)

    ts = pd.to_datetime(obs["timestamp"])
    fig2, ax = plt.subplots(figsize=(14, 5))
    ax.plot(ts, obs["mid_price"].astype(float), lw=0.8, color="#34495e")
    for t, color, marker in [("Depletion", "#c0392b", "v"), ("WallFormation", "#8e44ad", "^")]:
        g = ev[ev["type"] == t]
        if len(g):
            ax.scatter(pd.to_datetime(g["timestamp"]), g["mid_price"].astype(float), s=8, color=color,
                       marker=marker, alpha=0.6, label=t)
    ax.set_xlabel("time UTC")
    ax.set_ylabel("mid price (USDT)")
    ax.set_title("Mid price with depletion (▼) and wall-formation (▲) events")
    ax.legend()
    fig2.tight_layout()
    fig2.savefig(out / "price_with_events.png", dpi=140)

    fig3, ax = plt.subplots(figsize=(8, 5))
    dep = ev[ev["type"] == "Depletion"]
    dep_lat = dep[dep["ms_until_replenishment"].astype(float) > 0]["ms_until_replenishment"].astype(float)
    if len(dep_lat):
        ax.hist(dep_lat / 1000, bins=40, color="#c0392b", alpha=0.8)
        ax.set_xlabel("seconds until replenishment")
        ax.set_title("Depletion -> replenishment latency distribution")
    else:
        ax.text(0.5, 0.5, "no latencies", ha="center")
    fig3.tight_layout()
    fig3.savefig(out / "replenishment_latency.png", dpi=140)

    with open(out / "liquidity_trend_report.md", "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    print("\n".join(lines))


if __name__ == "__main__":
    main()