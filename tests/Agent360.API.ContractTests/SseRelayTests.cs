using System.IO.Pipelines;
using System.Text;
using Agent360.API.Features.Agents.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class SseRelayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Silent_upstream_gets_keepalive_comments_then_data_and_completes()
    {
        var pipe = new Pipe();
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;

        var relay = SseRelay.CopyAsync(pipe.Reader.AsStream(), ctx.Response, TimeSpan.FromMilliseconds(50), Ct);
        await Task.Delay(220, Ct);                       // upstream silent => several pings
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("data: {\"x\":1}\n\n"), Ct);
        await pipe.Writer.CompleteAsync();
        await relay.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.True(text.Split(": ping").Length - 1 >= 2, text);
        Assert.EndsWith("data: {\"x\":1}\n\n", text);
        Assert.Equal("no", ctx.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public async Task Fast_upstream_is_copied_verbatim_without_pings()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var src = new MemoryStream(Encoding.UTF8.GetBytes("data: a\n\ndata: b\n\n"));
        await SseRelay.CopyAsync(src, ctx.Response, TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("data: a\n\ndata: b\n\n", Encoding.UTF8.GetString(body.ToArray()));
    }
}
