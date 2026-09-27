using System;
using System.Collections.Generic;
using System.Threading;
using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// Turns wall-clock time into observation-frontier advances for live runs.
    ///
    /// In backtest the frontier moves because events arrive, and the end of the data ends the run. In
    /// live there is no end of the data, and a quiet market produces no events at all — so a purely
    /// event-driven frontier stalls until trading resumes, and the live run then disagrees with a
    /// backtest over the same events. This stream emits a tick on every observation-grid boundary so
    /// the frontier keeps moving and the quiet periods are published as what they are: padding,
    /// stamped <c>data_quality=filled</c>.
    ///
    /// Ticks land exactly on grid boundaries and nowhere else. That is the only placement that is
    /// correct: a tick closes the periods that have fully elapsed, so one emitted at a boundary closes
    /// everything before it and leaves the boundary's own period open for the events still to come.
    /// A tick emitted early would close a period before it ended and make every later event in it look
    /// late, which would be reported as a source-contract violation that the source did not commit.
    /// </summary>
    public static class ObservationClockStream
    {
        /// <summary>
        /// Emits a tick on each observation-grid boundary in <c>(from, until]</c>, waiting until each
        /// boundary is actually reached.
        /// </summary>
        /// <param name="origin">Phase of the observation grid; must match the replay engine's.</param>
        /// <param name="interval">Observation cadence.</param>
        /// <param name="from">Never ticks earlier than this, so a live run does not pad from a
        /// <c>startTime</c> that is already in the past.</param>
        /// <param name="until">Exclusive upper bound; null means run until cancelled.</param>
        /// <param name="utcNow">Time source, injected so the wait is testable without real time.</param>
        /// <param name="pollInterval">How often to re-check the clock while waiting.</param>
        /// <param name="cancellation">Stops the stream; a cancelled live run must not wait out its
        /// remaining duration.</param>
        public static IEnumerable<MarketEvent> Boundaries(
            DateTime origin,
            TimeSpan interval,
            DateTime from,
            DateTime? until,
            Func<DateTime> utcNow,
            TimeSpan pollInterval,
            CancellationToken cancellation)
        {
            if (interval <= TimeSpan.Zero)
            {
                yield break;
            }

            var next = FirstUsefulBoundary(origin, interval, from);

            while (until == null || next <= until.Value)
            {
                if (cancellation.IsCancellationRequested)
                {
                    yield break;
                }

                var now = utcNow();
                if (now < next)
                {
                    // Sleep in bounded slices so cancellation is observed promptly and a long wait
                    // does not pin the thread.
                    var remaining = next - now;
                    var wait = remaining < pollInterval ? remaining : pollInterval;
                    if (wait > TimeSpan.Zero)
                    {
                        if (cancellation.WaitHandle.WaitOne(wait))
                        {
                            yield break;
                        }
                    }

                    continue;
                }

                // A clock that jumped forward (suspend, NTP correction) can leave several boundaries
                // already due. Emitting each one in turn keeps the engine's per-period close-and-pad
                // rule in charge of how they surface, rather than collapsing them here.
                yield return new ClockTickEvent(next);
                next += interval;
            }
        }

        /// <summary>
        /// The first grid point at or after <paramref name="from"/>. Uses ceiling division on the grid
        /// phase so the result lands on the same points the replay engine will use.
        /// </summary>
        internal static DateTime FirstBoundaryOnOrAfter(DateTime origin, TimeSpan interval, DateTime from)
        {
            if (from <= origin)
            {
                return origin;
            }

            var elapsed = from - origin;
            var steps = (long)Math.Ceiling(elapsed.Ticks / (double)interval.Ticks);
            var next = origin + TimeSpan.FromTicks(steps * interval.Ticks);
            return next < from ? next + interval : next;
        }

        /// <summary>
        /// The first boundary a tick can actually do something with.
        ///
        /// A tick closes the periods strictly before the one containing it, so a tick on a boundary
        /// that has already been reached — <paramref name="from"/> itself, or the very first grid point
        /// — closes nothing and only costs a wake-up. Skipping those keeps the stream from spending
        /// polls on no-ops, which matters because it competes with real data for the merge.
        /// </summary>
        private static DateTime FirstUsefulBoundary(DateTime origin, TimeSpan interval, DateTime from)
        {
            var next = FirstBoundaryOnOrAfter(origin, interval, from);
            if (next == from || next == origin)
            {
                next += interval;
            }

            return next;
        }
    }
}
