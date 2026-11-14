using System.Net.Sockets;

namespace VoiceWink.Helpers;

/// <summary>LAI-18: a server quit before it replies can leave the request's connection open, so only refused connects, <see cref="RefusalsToLose"/> in a row, end the reply early.</summary>
internal sealed record LocalServerPresence(TimeSpan Interval, int RefusalsToLose, Func<Uri, CancellationToken, Task<bool>> IsRefused)
{
    internal static readonly LocalServerPresence Default = new(TimeSpan.FromSeconds(5), 2, TcpConnectRefusedAsync);

    /// <summary>Windows retries a refused SYN for about 2 s per loopback address, and "localhost" tries two (LAI-18).</summary>
    internal static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(10);

    /// <summary>True only for a refused connect: a slow, busy or unreachable server is never read as gone.</summary>
    internal static async Task<bool> TcpConnectRefusedAsync(Uri address, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ConnectBudget);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address.IdnHost, address.Port, budget.Token).ConfigureAwait(false);
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
