using System.Net.WebSockets;
using System.Text;

namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// WebSocket transport abstraction so live connectors can be scripted with fake frames in tests.
    /// </summary>
    public interface IWsTransport
    {
        /// <summary>
        /// Opens a client connection to the given URI.
        /// </summary>
        Task ConnectAsync(string uri, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a text message frame.
        /// </summary>
        Task SendTextAsync(string message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Receives the next complete text message, or null when the peer closes the connection.
        /// </summary>
        Task<string> ReceiveTextAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes the connection and releases resources.
        /// </summary>
        Task CloseAsync();
    }

    /// <summary>
    /// Production WebSocket transport backed by <see cref="ClientWebSocket"/>.
    /// </summary>
    public sealed class ClientWebSocketTransport : IWsTransport, IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Memory<byte> _buffer = new byte[64 * 1024];

        public async Task ConnectAsync(string uri, CancellationToken cancellationToken = default)
        {
            await _socket.ConnectAsync(new Uri(uri), cancellationToken).ConfigureAwait(false);
        }

        public async Task SendTextAsync(string message, CancellationToken cancellationToken = default)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                stream.Write(_buffer.Span[..result.Count]);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public async Task CloseAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (WebSocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            _socket.Dispose();
        }

        public void Dispose()
        {
            _ = CloseAsync();
        }
    }
}