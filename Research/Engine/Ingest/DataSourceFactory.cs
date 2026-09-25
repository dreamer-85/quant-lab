using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;

namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// Resolves the concrete <see cref="IEventDataSource"/> for a job based on its
    /// <see cref="ResearchJob.Source"/> configuration. Jobs without an exchange source keep the
    /// existing Lean zip-backed reader; "historical"/"live" jobs get the <see cref="ExchangeDataAdapter"/>,
    /// "archive" jobs get the <see cref="Bybit.BybitArchiveSource"/> replay, and "feed" jobs
    /// (DataFeeds staging) get the <see cref="LocalFeed.LocalFeedDataSource"/>.
    /// </summary>
    public static class DataSourceFactory
    {
        public static IEventDataSource Create(ResearchJob job, ResearchEnvironment environment)
        {
            if (job?.Source != null && job.Source.IsExchangeSource())
            {
                if (job.Source.Mode.Equals("archive", StringComparison.OrdinalIgnoreCase))
                {
                    return new Bybit.BybitArchiveSource(job, job.Source);
                }

                if (job.Source.Mode.Equals("feed", StringComparison.OrdinalIgnoreCase))
                {
                    return new LocalFeed.LocalFeedDataSource(job, job.Source, environment.DataRoot);
                }

                return new ExchangeDataAdapter(job, job.Source);
            }

            return new LeanDataEventSource(new LeanDataEventReader(environment.DataRoot));
        }
    }
}