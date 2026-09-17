using System.Globalization;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Liquidity;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Detects per-side liquidity transitions (depletion, replenishment, migration, wall formation)
    /// on each observation and emits one row per transition plus per-horizon forward outcomes
    /// (ret / MFE / MAE vs that observation's mid) resolved from subsequent observations.
    /// Aggregates point metrics (rates, executed fractions, mean magnitudes, replenishment
    /// durations) into <see cref="ExperimentResult.Metrics"/>.
    ///
    /// Config keys (job.ExperimentConfig):
    ///   horizons                  "60s,5m"  comma list of forecast horizons (default job.Horizons)
    ///   bps_band                  10        depth band around mid in bps
    ///   depletion_fraction        0.25      min relative depth loss to count as depletion
    ///   depletion_min_size        0.01      absolute (base) depletion floor
    ///   replenishment_fraction    0.25      min relative depth gain to count as replenishment
    ///   migration_bps             5         min best-price move (bps) to count as migration
    ///   wall_multiple             5         best-level / median level size ratio for a wall
    ///   replenishment_lookback_ms 30000     window that still back-fills a depletion duration
    ///   max_observation_gap_ms    2000      max gap between observations for before/after validity
    ///
    /// Memory is bounded: only price snapshots within the longest horizon and the transition
    /// event rows are retained. Observation rows themselves are NOT duplicated here (the executor
    /// already persists them) so analysis can join by (symbol, timestamp).
    /// </summary>
    public sealed class LiquidityTrendExperiment : ExperimentBase
    {
        private LiquidityDetector _detector;
        private readonly List<TimeSpan> _horizons = new();
        private readonly List<string> _horizonLabels = new();
        private readonly Dictionary<string, PriceRing> _rings = new(StringComparer.Ordinal);
        private readonly List<PendingEvent> _pending = new();
        private readonly List<Dictionary<string, object>> _rows = new();
        private long _eventSeq;
        private long _observationCount;

        // Metrics accumulators.
        private readonly long[] _typeCounts = new long[Enum.GetValues(typeof(LiquidityTransitionType)).Length];
        private readonly long[,] _typeSideCounts = new long[Enum.GetValues(typeof(LiquidityTransitionType)).Length, 2];
        private long _executedDepletionCount;
        private long _depletionCount;
        private double _sumDepletionDelta;
        private double _sumReplenishmentDelta;
        private double _sumDepletionNetFlow;
        private double _sumReplenishmentNetFlow;
        private double _sumReplenishmentDuration;
        private long _replenishmentDurationCount;
        private double _maxObservedSpanMs;
        private double _sumEventSpacingMs;
        private long _eventSpacingCount;

        public LiquidityTrendExperiment()
            : base(maxRetainedObservations: 0, maxRetainedOutcomes: 0)
        {
            _detector = new LiquidityDetector();
        }

        public override string Name => "liquidity_trend";

        public override string Description =>
            "Liquidity depletion/replenishment transitions vs forward trend outcomes";

        public override string Version => "2.0.0";

        public override List<string> RequiredFeatures => new();

        public override void Initialize(ExperimentContext context)
        {
            base.Initialize(context);

            var detectorConfig = new LiquidityDetectorConfig
            {
                BpsBand = context.GetConfigDecimal("bps_band", 10m),
                DepletionFraction = context.GetConfigDecimal("depletion_fraction", 0.25m),
                DepletionMinSize = context.GetConfigDecimal("depletion_min_size", 0.01m),
                ReplenishmentFraction = context.GetConfigDecimal("replenishment_fraction", 0.25m),
                MigrationBps = context.GetConfigDecimal("migration_bps", 5m),
                WallMultiple = context.GetConfigDecimal("wall_multiple", 5m),
                ReplenishmentLookbackMs = context.GetConfigDecimal("replenishment_lookback_ms", 30000m),
                MaxObservationGapMs = context.GetConfigDecimal("max_observation_gap_ms", 2000m)
            };
            _detector = new LiquidityDetector(detectorConfig);

            // Horizons: explicit config wins, otherwise the job-level Horizons list.
            var horizonText = context.GetConfig("horizons", string.Empty);
            if (string.IsNullOrWhiteSpace(horizonText))
                horizonText = context.GetConfig("liquidity_horizons", string.Empty);

            _horizons.Clear();
            _horizonLabels.Clear();
            if (!string.IsNullOrWhiteSpace(horizonText))
            {
                foreach (var token in horizonText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _horizons.Add(HorizonParser.Parse(token));
                    _horizonLabels.Add(SanitizeColumn(token));
                }
            }

            _rings.Clear();
            _pending.Clear();
            _rows.Clear();
            _eventSeq = 0;
            _observationCount = 0;
            Array.Clear(_typeCounts, 0, _typeCounts.Length);
            Array.Clear(_typeSideCounts, 0, _typeSideCounts.Length);
            _executedDepletionCount = 0;
            _depletionCount = 0;
            _sumDepletionDelta = 0d;
            _sumReplenishmentDelta = 0d;
            _sumDepletionNetFlow = 0d;
            _sumReplenishmentNetFlow = 0d;
            _sumReplenishmentDuration = 0d;
            _replenishmentDurationCount = 0;
            _maxObservedSpanMs = 0d;
            _sumEventSpacingMs = 0d;
            _eventSpacingCount = 0;
        }

        public override void OnObservation(Observation observation, FeatureResult features)
        {
            if (observation?.State?.Symbol == null || string.IsNullOrEmpty(observation.State.Symbol.Value))
                return;

            var symbol = observation.State.Symbol.Value;
            _observationCount++;

            if (!_rings.TryGetValue(symbol, out var ring))
            {
                ring = new PriceRing(_horizons.Count > 0 ? _horizons[^1] : TimeSpan.FromSeconds(5));
                _rings[symbol] = ring;
            }

            var snapshotTicks = observation.Timestamp.Ticks;
            var snapshotMid = (double)observation.State.MidPrice;
            ring.Append(snapshotTicks, snapshotMid);

            // Snapshots only resolve rows for their own symbol.
            if (_pending.Count > 0)
            {
                ResolveAndExtend(symbol, snapshotTicks, snapshotMid);
            }

            foreach (var evt in _detector.Observe(observation))
            {
                var row = BuildEventRow(evt);
                _rows.Add(row);

                if (_horizons.Count > 0)
                {
                    var pending = new PendingEvent(evt, row, _horizons, _horizonLabels, ring);
                    _pending.Add(pending);
                }

                AccumulateMetrics(evt, symbol);
            }
        }

        public override ExperimentResult Finalize()
        {
            var result = base.Finalize();

            // Resolve leftover transition rows against each symbol's last observed price so no row
            // is dropped from the dataset; resolved=false signals terminal-not-realized.
            foreach (var pending in _pending)
            {
                if (pending.AllResolved)
                    continue;

                if (_rings.TryGetValue(pending.Symbol, out var ring))
                {
                    var terminal = ring.LastMid(pending.EventTimeMs);
                    if (terminal.HasValue)
                    {
                        FinalizeUnresolved(pending, terminal.Value);
                    }
                }
            }

            EmitMetrics(result);

            result.Rows = _rows;
            result.Metadata["event_count"] = _rows.Count.ToString(CultureInfo.InvariantCulture);
            result.Metadata["horizons"] = string.Join(",", _horizonLabels);
            return result;
        }

        private Dictionary<string, object> BuildEventRow(LiquidityTransitionEvent evt)
        {
            var row = new Dictionary<string, object>
            {
                ["event_id"] = ++_eventSeq,
                ["symbol"] = evt.Symbol,
                ["timestamp"] = evt.Timestamp,
                ["type"] = evt.Type.ToString(),
                ["side"] = evt.Side.ToString(),
                ["price"] = (double)evt.Price,
                ["mid_price"] = (double)evt.MidPrice,
                ["bps_band"] = (double)evt.BpsBand,
                ["depth_before"] = (double)evt.DepthBefore,
                ["depth_after"] = (double)evt.DepthAfter,
                ["depth_delta"] = (double)evt.DepthDelta,
                ["executed"] = evt.Executed,
                ["executed_volume"] = (double)evt.ExecutedVolume,
                ["aggressive_buy_volume"] = (double)evt.AggressiveBuyVolume,
                ["aggressive_sell_volume"] = (double)evt.AggressiveSellVolume,
                ["net_flow"] = (double)evt.NetFlow,
                ["update_count"] = evt.UpdateCount,
                ["remove_count"] = evt.RemoveCount,
                ["add_count"] = evt.AddCount,
                ["trade_count"] = evt.TradeCount,
                ["spread_bps"] = (double)evt.SpreadBps,
                ["ms_since_last_event"] = evt.MsSinceLastEvent >= 0 ? (double)evt.MsSinceLastEvent : -1d,
                ["ms_until_replenishment"] = evt.MsUntilReplenishment.HasValue ? (double)evt.MsUntilReplenishment.Value : 0d,
                ["replenished_within_lookback"] = evt.MsUntilReplenishment.HasValue
            };

            // Horizon outcome placeholders are always present so the parquet schema is stable.
            // They are filled in-place as future observations resolve them.
            foreach (var label in _horizonLabels)
            {
                row[$"ret_{label}"] = 0d;
                row[$"mfe_{label}"] = 0d;
                row[$"mae_{label}"] = 0d;
                row[$"resolved_{label}"] = false;
            }

            return row;
        }

        /// <summary>
        /// Advances pending rows using the new snapshot: extremes accumulate for still-open
        /// horizons, and horizons whose target time has now elapsed are resolved against the last
        /// snapshot at-or-before the target.
        /// </summary>
        private void ResolveAndExtend(string symbol, long snapshotTicks, double snapshotMid)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                if (pending.Symbol != symbol)
                    continue;

                foreach (var target in pending.Targets)
                {
                    if (target.Resolved)
                        continue;

                    // The snapshot at the event time itself is the reference, never part of the extremes.
                    if (snapshotTicks > pending.EventTimeMs && snapshotTicks <= target.TargetMs)
                    {
                        if (snapshotMid > target.MaxMid) target.MaxMid = snapshotMid;
                        if (target.MinMid == 0d || snapshotMid < target.MinMid) target.MinMid = snapshotMid;
                    }

                    if (snapshotTicks >= target.TargetMs && pending.Ring.TryGetLastMid(target.TargetMs, out var terminal))
                    {
                        ApplyOutcome(target, pending, terminal, markResolved: true);
                        pending.ResolvedCount++;
                    }
                }

                if (pending.AllResolved)
                {
                    _pending.RemoveAt(i);
                }
            }
        }

        private static void FinalizeUnresolved(PendingEvent pending, double lastMid)
        {
            foreach (var target in pending.Targets)
            {
                if (!target.Resolved)
                {
                    ApplyOutcome(target, pending, lastMid, markResolved: false);
                    pending.ResolvedCount++;
                }
            }
        }

        private static void ApplyOutcome(HorizonTarget target, PendingEvent pending, double terminal, bool markResolved)
        {
            target.Resolved = true;
            var referenceMid = pending.ReferenceMid;

            var maxFactor = target.MinMid == 0d && target.MaxMid == 0d
                ? terminal
                : Math.Max(target.MaxMid, terminal);
            var minFactor = target.MinMid == 0d && target.MaxMid == 0d
                ? terminal
                : Math.Min(target.MinMid == 0d ? terminal : target.MinMid, terminal);

            var ret = referenceMid > 0d ? (terminal - referenceMid) / referenceMid : 0d;
            var mfe = referenceMid > 0d ? (maxFactor - referenceMid) / referenceMid : 0d;
            var mae = referenceMid > 0d ? (minFactor - referenceMid) / referenceMid : 0d;

            pending.Row[$"ret_{target.Label}"] = Math.Round(ret, 8);
            pending.Row[$"mfe_{target.Label}"] = Math.Round(mfe, 8);
            pending.Row[$"mae_{target.Label}"] = Math.Round(mae, 8);
            if (markResolved)
                pending.Row[$"resolved_{target.Label}"] = true;
        }

        private void EmitMetrics(ExperimentResult result)
        {
            const int depletion = (int)LiquidityTransitionType.Depletion;
            const int replenishment = (int)LiquidityTransitionType.Replenishment;
            const int migration = (int)LiquidityTransitionType.Migration;
            const int wall = (int)LiquidityTransitionType.WallFormation;

            result.AddMetric("observation_count", _observationCount);
            result.AddMetric("event_count", _rows.Count);
            result.AddMetric("event_rate_per_minute",
                _maxObservedSpanMs > 0d ? Math.Round(_rows.Count / (_maxObservedSpanMs / 60000d), 3) : 0d);

            result.AddMetric("depletion_count", _typeCounts[depletion]);
            result.AddMetric("replenishment_count", _typeCounts[replenishment]);
            result.AddMetric("migration_count", _typeCounts[migration]);
            result.AddMetric("wall_formation_count", _typeCounts[wall]);

            result.AddMetric("depletion_bid_count", _typeSideCounts[depletion, (int)OrderBookSide.Bid]);
            result.AddMetric("depletion_ask_count", _typeSideCounts[depletion, (int)OrderBookSide.Ask]);
            result.AddMetric("replenishment_bid_count", _typeSideCounts[replenishment, (int)OrderBookSide.Bid]);
            result.AddMetric("replenishment_ask_count", _typeSideCounts[replenishment, (int)OrderBookSide.Ask]);
            result.AddMetric("migration_bid_count", _typeSideCounts[migration, (int)OrderBookSide.Bid]);
            result.AddMetric("migration_ask_count", _typeSideCounts[migration, (int)OrderBookSide.Ask]);

            result.AddMetric("executed_depletion_count", _executedDepletionCount);
            result.AddMetric("executed_depletion_fraction",
                _depletionCount > 0 ? Math.Round(_executedDepletionCount / (double)_depletionCount, 4) : 0d);

            result.AddMetric("mean_depletion_depth_delta",
                _typeCounts[depletion] > 0 ? Math.Round(_sumDepletionDelta / _typeCounts[depletion], 6) : 0d);
            result.AddMetric("mean_replenishment_depth_delta",
                _typeCounts[replenishment] > 0 ? Math.Round(_sumReplenishmentDelta / _typeCounts[replenishment], 6) : 0d);

            result.AddMetric("mean_net_flow_on_depletion",
                _typeCounts[depletion] > 0 ? Math.Round(_sumDepletionNetFlow / _typeCounts[depletion], 4) : 0d);
            result.AddMetric("mean_net_flow_on_replenishment",
                _typeCounts[replenishment] > 0 ? Math.Round(_sumReplenishmentNetFlow / _typeCounts[replenishment], 4) : 0d);

            result.AddMetric("mean_ms_until_replenishment",
                _replenishmentDurationCount > 0 ? Math.Round(_sumReplenishmentDuration / _replenishmentDurationCount, 1) : 0d);
            result.AddMetric("replenishment_backfilled_count", _replenishmentDurationCount);
            result.AddMetric("mean_event_spacing_ms",
                _eventSpacingCount > 0 ? Math.Round(_sumEventSpacingMs / _eventSpacingCount, 1) : 0d);
        }

        private void AccumulateMetrics(LiquidityTransitionEvent evt, string symbol)
        {
            var typeIndex = (int)evt.Type;
            _typeCounts[typeIndex]++;
            _typeSideCounts[typeIndex, (int)evt.Side]++;

            switch (evt.Type)
            {
                case LiquidityTransitionType.Depletion:
                    _depletionCount++;
                    _sumDepletionDelta += (double)evt.DepthDelta;
                    _sumDepletionNetFlow += (double)evt.NetFlow;
                    if (evt.Executed)
                        _executedDepletionCount++;
                    break;
                case LiquidityTransitionType.Replenishment:
                    _sumReplenishmentDelta += (double)evt.DepthDelta;
                    _sumReplenishmentNetFlow += (double)evt.NetFlow;
                    if (evt.MsUntilReplenishment.HasValue)
                    {
                        _sumReplenishmentDuration += evt.MsUntilReplenishment.Value;
                        _replenishmentDurationCount++;
                    }
                    break;
            }

            if (evt.MsSinceLastEvent >= 0)
            {
                _sumEventSpacingMs += evt.MsSinceLastEvent;
                _eventSpacingCount++;
            }

            if (_rings.TryGetValue(symbol, out var ring) && ring.SpanMs > _maxObservedSpanMs)
                _maxObservedSpanMs = ring.SpanMs;
        }

        private static string SanitizeColumn(string text)
        {
            var chars = text.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars);
        }

        /// <summary>
        /// Bounded ring of observed mid-price snapshots for one symbol. Only entries within the
        /// longest horizon of the stream are retained.
        /// </summary>
        private sealed class PriceRing
        {
            private readonly long _maxSpanTicks;
            private readonly List<(long Ticks, double Mid)> _snapshots = new();
            private long _lastTicks = long.MinValue;

            public double SpanMs { get; private set; }

            public PriceRing(TimeSpan maxHorizon)
            {
                _maxSpanTicks = maxHorizon.Ticks > 0 ? maxHorizon.Ticks : TimeSpan.FromSeconds(5).Ticks;
            }

            public void Append(long ticks, double mid)
            {
                var cutoff = ticks - _maxSpanTicks;
                while (_snapshots.Count > 0 && _snapshots[0].Ticks < cutoff)
                {
                    _snapshots.RemoveAt(0);
                }

                if (mid > 0d && ticks > _lastTicks)
                {
                    _snapshots.Add((ticks, mid));
                    _lastTicks = ticks;
                }

                if (_snapshots.Count > 0)
                {
                    SpanMs = Math.Max(SpanMs, (ticks - _snapshots[0].Ticks) / TimeSpan.TicksPerMillisecond);
                }
            }

            public double? LastMid(long atOrBeforeTicks)
            {
                if (_snapshots.Count == 0) return null;
                var idx = FindLastAtOrBefore(atOrBeforeTicks);
                return _snapshots[idx].Mid;
            }

            public bool TryGetLastMid(long atOrBeforeTicks, out double mid)
            {
                if (_snapshots.Count == 0)
                {
                    mid = 0d;
                    return false;
                }

                mid = _snapshots[FindLastAtOrBefore(atOrBeforeTicks)].Mid;
                return true;
            }

            private int FindLastAtOrBefore(long ticks)
            {
                int lo = 0, hi = _snapshots.Count - 1, ans = 0;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    if (_snapshots[mid].Ticks <= ticks)
                    {
                        ans = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                return ans;
            }
        }

        /// <summary>One event row awaiting forward-outcome resolution for each horizon.</summary>
        private sealed class PendingEvent
        {
            public string Symbol;
            public long EventTimeMs;
            public double ReferenceMid;
            public PriceRing Ring;
            public Dictionary<string, object> Row;
            public List<HorizonTarget> Targets = new();
            public int ResolvedCount;

            public bool AllResolved => ResolvedCount >= Targets.Count;

            public PendingEvent(LiquidityTransitionEvent evt, Dictionary<string, object> row,
                List<TimeSpan> horizons, List<string> labels, PriceRing ring = null)
            {
                Symbol = evt.Symbol;
                EventTimeMs = evt.Timestamp.Ticks;
                ReferenceMid = (double)evt.MidPrice;
                Ring = ring;
                Row = row;

                for (var h = 0; h < horizons.Count; h++)
                {
                    Targets.Add(new HorizonTarget
                    {
                        HorizonIndex = h,
                        Label = labels[h],
                        TargetMs = evt.Timestamp.Ticks + horizons[h].Ticks
                    });
                }
            }
        }

        /// <summary>Forward outcome state for one event + one horizon, resolved onto the row in place.</summary>
        private sealed class HorizonTarget
        {
            public int HorizonIndex;
            public string Label;
            public long TargetMs;
            public bool Resolved;
            public double MaxMid;
            public double MinMid;
        }
    }
}