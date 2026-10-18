using System.Net;
using Agent360.API.Configurations;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agent360.API.ContractTests;

/// <summary>Regression: with no Logto authority configured, forged/unsigned JWTs must never authenticate outside Development.</summary>
public sealed class AuthenticationFailClosedTests
{
    private const string Unsigned = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.";
    private const string FakeSigned = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4IiwiZXhwIjo5OTk5OTk5OTk5fQ.c2ln";

    [Theory]
    [InlineData("Production", Unsigned, false)]
    [InlineData("Staging", Unsigned, false)]
    [InlineData("Staging", FakeSigned, false)]
    [InlineData("Development", Unsigned, true)]
    public async Task ForgedBearer_WithoutLogtoAuthority_IsRejectedOutsideDevelopment(string environment, string token, bool expectAuthenticated)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Configuration["Logto:Authority"] = "";
        b.Configuration["Logto:Audience"] = "";
        b.ConfigureAuthentication();
        b.Services.AddAuthorization();
        await using var app = b.Build();
        app.UseAuthentication();
        app.MapGet("/whoami", (HttpContext c) => Results.Ok(c.User.Identity?.IsAuthenticated == true));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var res = await http.GetAsync("/whoami", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(expectAuthenticated ? "true" : "false");
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
