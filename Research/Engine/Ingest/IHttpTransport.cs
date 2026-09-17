namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// Minimal HTTP transport abstraction so connectors can be driven by fake/mocked
    /// responses in tests without a network.
    /// </summary>
    public interface IHttpTransport
    {
        /// <summary>
        /// Sends an HTTP request and returns the response.
        /// </summary>
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Production HTTP transport backed by <see cref="HttpClient"/>.
    /// </summary>
    public sealed class HttpClientTransport : IHttpTransport, IDisposable
    {
        private readonly HttpClient _http;

        public HttpClientTransport()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            return _http.SendAsync(request, cancellationToken);
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}