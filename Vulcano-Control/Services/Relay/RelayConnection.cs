using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Vulcano_Control.Services.Relay;

/// <summary>
/// Wraps a single TCP connection's line-framed <see cref="RelayMessage"/> read/write plumbing,
/// shared by both the server side (one instance per connected client) and the client side (one
/// instance for its connection to the server). Owns an outbound <see cref="Channel{T}"/> plus a
/// single writer-pump task so unrelated callers (e.g. a request's response vs. a fanned-out event)
/// can never interleave writes on the same <see cref="NetworkStream"/> - concurrent WriteAsync
/// calls on one stream aren't guaranteed atomic and could otherwise corrupt the line framing.
/// </summary>
public sealed class RelayConnection : IAsyncDisposable
{
    private const int MaxLineLengthBytes = 64 * 1024;
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly Channel<RelayMessage> _outbox = Channel.CreateUnbounded<RelayMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerPump;

    public RelayConnection(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writerPump = RunWriterPumpAsync();
    }

    /// <summary>Signaled once the connection is torn down (transport failure or explicit Dispose).</summary>
    public CancellationToken Closed => _cts.Token;

    /// <summary>Enqueues a message for sending; returns immediately, never blocks on network I/O.</summary>
    public void Send(RelayMessage message) => _outbox.Writer.TryWrite(message);

    /// <summary>
    /// Reads and parses the next line-framed message, or null on clean EOF, malformed JSON, an
    /// oversized line, or cancellation - callers should treat null as "connection is done."
    /// </summary>
    public async Task<RelayMessage?> ReceiveAsync(CancellationToken ct)
    {
        string? line;
        try
        {
            line = await _reader.ReadLineAsync(ct);
        }
        catch
        {
            return null;
        }

        if (line is null || Encoding.UTF8.GetByteCount(line) > MaxLineLengthBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RelayMessage>(line, RelayJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private async Task RunWriterPumpAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RelayJson.Options);
                await _stream.WriteAsync(bytes, _cts.Token);
                await _stream.WriteAsync(NewLine, _cts.Token);
            }
        }
        catch
        {
            // Best-effort: a write failure just means the connection is dead, which ReceiveAsync's
            // caller will independently discover and react to.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;

        _cts.Cancel();
        _outbox.Writer.TryComplete();
        try
        {
            await _writerPump;
        }
        catch
        {
            // Already best-effort inside the pump; nothing further to do here.
        }

        _reader.Dispose();
        await _stream.DisposeAsync();
        _tcpClient.Dispose();
        _cts.Dispose();
    }
}
