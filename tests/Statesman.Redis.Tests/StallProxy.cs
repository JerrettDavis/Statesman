using System.Net;
using System.Net.Sockets;

namespace Statesman.Redis.Tests;

/// <summary>
/// A loopback TCP proxy in front of a real Redis, with one switch: once <see cref="Stall"/> is set,
/// bytes the client writes stop being forwarded, so every command issued after that point is on the
/// wire from the client's point of view and is never answered, while the socket stays open.
/// <para>
/// This is the Phase 19 lease-split "injected stall" pattern applied to a socket instead of a clock.
/// It is what makes a dispose racing an in-flight SUBSCRIBE deterministic rather than a flake: it
/// needs no container pause, no sleep-and-hope, and no new production seam.
/// </para>
/// </summary>
internal sealed class StallProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _accepting;

    private StallProxy(TcpListener listener, string upstreamHost, int upstreamPort)
    {
        _listener = listener;
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _accepting = Task.Run(AcceptLoopAsync);
    }

    /// <summary>When true, client-to-upstream bytes are swallowed instead of forwarded.</summary>
    public volatile bool Stall;

    /// <summary>The loopback port a client should connect to instead of Redis.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts a proxy in front of the given <c>host:port</c> upstream.</summary>
    /// <param name="upstream">The real Redis endpoint, as <c>host:port</c>.</param>
    /// <returns>A running proxy, listening on an ephemeral loopback port.</returns>
    public static StallProxy Start(string upstream)
    {
        string[] parts = upstream.Split(':');
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new StallProxy(listener, parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6379);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ProxyAsync(client));
        }
    }

    private async Task ProxyAsync(TcpClient client)
    {
        using TcpClient upstream = new();
        try
        {
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _shutdown.Token);
            using NetworkStream fromClient = client.GetStream();
            using NetworkStream toUpstream = upstream.GetStream();
            await Task.WhenAny(
                PumpAsync(fromClient, toUpstream, honourStall: true),
                PumpAsync(toUpstream, fromClient, honourStall: false));
        }
        catch (Exception)
        {
            // A torn-down proxy is the expected end of every connection here.
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task PumpAsync(Stream source, Stream destination, bool honourStall)
    {
        byte[] buffer = new byte[8192];
        while (!_shutdown.IsCancellationRequested)
        {
            int read = await source.ReadAsync(buffer, _shutdown.Token);
            if (read == 0)
            {
                return;
            }

            if (honourStall && Stall)
            {
                // Swallowed, not forwarded: the command is on the wire from the client's point of
                // view and will never be answered.
                continue;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token);
            await destination.FlushAsync(_shutdown.Token);
        }
    }

    /// <summary>Stops listening and tears down every proxied connection.</summary>
    /// <returns>A task that completes once the accept loop has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception)
        {
        }

        _shutdown.Dispose();
    }
}
