namespace Agent360.API.Features.Agents.Endpoints;

/// <summary>
/// Relays an upstream SSE body to the client and writes an SSE comment (<c>: ping</c>) whenever the upstream is
/// silent for the keep-alive interval. Long agent turns (tool calls, sub-agent delegation) can stay silent
/// for minutes; without bytes Cloudflare cuts the request after ~100 s (524) and the browser loses the stream.
/// Comment lines are ignored by every SSE parser.
/// </summary>
public static class SseRelay
{
    private static readonly byte[] Ping = ": ping\n\n"u8.ToArray();

    public static async Task CopyAsync(
        Stream upstream,
        HttpResponse response,
        TimeSpan keepAlive,
        CancellationToken ct,
        TimeSpan? maxIdleTimeout = null)
    {
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers.CacheControl = "no-cache";
        var output = response.Body;
        var buffer = new byte[16 * 1024];
        await response.StartAsync(ct);
        await output.FlushAsync(ct);
        Task<int>? pending = null;
        var effectiveIdleTimeout = maxIdleTimeout ?? TimeSpan.FromMinutes(10);
        var lastPayloadTime = DateTime.UtcNow;

        while (true)
        {
            pending ??= upstream.ReadAsync(buffer, ct).AsTask();
            var winner = await Task.WhenAny(pending, Task.Delay(keepAlive, ct));
            if (winner != pending)
            {
                if (DateTime.UtcNow - lastPayloadTime > effectiveIdleTimeout)
                {
                    // Upstream has been idle without payload events for longer than the idle threshold;
                    // gracefully close the relay to avoid holding open orphaned connections.
                    break;
                }

                await output.WriteAsync(Ping, ct);
                await output.FlushAsync(ct);
                continue;
            }
            var read = await pending;
            pending = null;
            if (read == 0)
            {
                break;
            }
            lastPayloadTime = DateTime.UtcNow;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            await output.FlushAsync(ct);
        }
    }
}
