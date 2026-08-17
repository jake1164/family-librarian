using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

/// <summary>
/// A loopback stand-in for clamd, speaking enough of the native protocol to
/// exercise <c>ClamAvMalwareScanner</c> without a container.
/// </summary>
/// <remarks>
/// Three behaviors of the real daemon matter here and are reproduced exactly,
/// because the client depends on all three:
/// <list type="number">
/// <item>Each command gets its own connection, and the daemon closes it after
/// replying — the client reads with <c>ReadToEndAsync</c>, so a reply that is
/// written but not followed by a close would hang forever.</item>
/// <item>A health check is <em>two</em> connections, PING then VERSION.</item>
/// <item>Commands arrive newline-terminated and "n"-prefixed
/// (<c>nINSTREAM\n</c>), and for INSTREAM the binary chunk stream follows
/// immediately on the same connection — so the command line must be read one
/// byte at a time rather than buffered, or the first chunk gets eaten.</item>
/// </list>
/// </remarks>
internal sealed class FakeClamdServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly string _pingResponse;
    private readonly string _versionResponse;
    private readonly string _instreamResponse;
    private readonly long? _abortAfterBytes;
    private readonly List<string> _commands = [];
    private readonly MemoryStream _payload = new();
    private readonly Lock _sync = new();

    private FakeClamdServer(
        string pingResponse,
        string versionResponse,
        string instreamResponse,
        long? abortAfterBytes)
    {
        _pingResponse = pingResponse;
        _versionResponse = versionResponse;
        _instreamResponse = instreamResponse;
        _abortAfterBytes = abortAfterBytes;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public int Port { get; }

    /// <summary>Commands the client sent, in order, with the "n" prefix stripped.</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_sync)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>The INSTREAM payload as reassembled from the client's length-prefixed chunks.</summary>
    public byte[] ReceivedPayload
    {
        get
        {
            lock (_sync)
            {
                return _payload.ToArray();
            }
        }
    }

    /// <summary>Whether the client sent the zero-length chunk that terminates INSTREAM.</summary>
    public bool SawStreamTerminator { get; private set; }

    public static FakeClamdServer Start(
        string pingResponse = "PONG\n",
        string versionResponse = "ClamAV 1.4.0/27000/Fri Aug 15 09:00:00 2026\n",
        string instreamResponse = "stream: OK\n",
        long? abortAfterBytes = null) =>
        new(pingResponse, versionResponse, instreamResponse, abortAfterBytes);

    /// <summary>A port with nothing listening on it, for the unreachable case.</summary>
    public static int FindUnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleConnectionAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (SocketException)
        {
            // Listener stopped underneath us.
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                if (_abortAfterBytes is not null)
                {
                    // Caps the TCP receive window so the OS can't just buffer
                    // the whole payload before the threshold check below ever
                    // runs. Without this, a large enough loopback send window
                    // (Linux's auto-tuned default is far bigger than Windows')
                    // lets the client finish writing everything before the
                    // abort happens, and the reset is never observed —
                    // exactly the flake this existed to prevent.
                    client.ReceiveBufferSize = 8 * 1024;
                }

                var stream = client.GetStream();
                var command = await ReadCommandLineAsync(stream, cancellationToken);
                if (command is null)
                {
                    return;
                }

                lock (_sync)
                {
                    _commands.Add(command);
                }

                if (string.Equals(command, "INSTREAM", StringComparison.Ordinal))
                {
                    if (!await ConsumeChunksAsync(client, stream, cancellationToken))
                    {
                        // Aborted mid-stream; the socket is already reset.
                        return;
                    }
                }

                var response = command switch
                {
                    "PING" => _pingResponse,
                    "VERSION" => _versionResponse,
                    _ => _instreamResponse
                };

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
                await stream.FlushAsync(cancellationToken);

                // The client reads to end-of-stream, so the reply only completes
                // once this side signals FIN.
                client.Client.Shutdown(SocketShutdown.Send);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // A client that went away mid-exchange is a scenario under test, not a failure.
        }
    }

    /// <summary>
    /// Reads the command line one byte at a time. Buffering would consume the
    /// first INSTREAM chunk along with the command.
    /// </summary>
    private static async Task<string?> ReadCommandLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var single = new byte[1];

        while (builder.Length < 64)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            if (single[0] == (byte)'\n')
            {
                var line = builder.ToString();
                return line.StartsWith('n') || line.StartsWith('z') ? line[1..] : line;
            }

            builder.Append((char)single[0]);
        }

        return null;
    }

    /// <summary>
    /// Consumes length-prefixed chunks until the zero-length terminator.
    /// Returns false if the connection was deliberately reset partway, which is
    /// how a real clamd behaves once a stream exceeds StreamMaxLength.
    /// </summary>
    private async Task<bool> ConsumeChunksAsync(
        TcpClient client,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[4];
        long total = 0;

        while (true)
        {
            if (!await ReadExactlyAsync(stream, lengthPrefix, cancellationToken))
            {
                return false;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
            if (length == 0)
            {
                SawStreamTerminator = true;
                return true;
            }

            var chunk = new byte[length];
            if (!await ReadExactlyAsync(stream, chunk, cancellationToken))
            {
                return false;
            }

            total += length;
            lock (_sync)
            {
                _payload.Write(chunk, 0, chunk.Length);
            }

            if (_abortAfterBytes is { } limit && total > limit)
            {
                // Linger 0 makes Close send RST rather than FIN, so the client's
                // next write fails instead of quietly succeeding into a closed
                // buffer — which is what makes the mid-stream failure observable.
                client.LingerState = new LingerOption(true, 0);
                client.Close();
                return false;
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _shutdown.Dispose();
        _payload.Dispose();
    }
}
