using Hmz.Core.SharedKernel.Http;
using Hmz.Core.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class ResultProblemDetailsTests
{
    [Theory]
    [InlineData(ErrorType.Failure, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Concurrency, StatusCodes.Status412PreconditionFailed)]
    [InlineData(ErrorType.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorType.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorType.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public async Task ExpectedError_MapsToStableProblemDetails(
        ErrorType errorType,
        int expectedStatus)
    {
        // Arrange
        var error = new Error("test.failure", "Safe failure message.", errorType);

        // Action
        using var response = await ExecuteAsync(Result.Failure(error));
        var root = response.RootElement;

        // Assertion
        root.GetProperty("status").GetInt32().Should().Be(expectedStatus);
        root.GetProperty("code").GetString().Should().Be("test.failure");
        root.GetProperty("detail").GetString().Should().Be("Safe failure message.");
        root.GetProperty("type").GetString().Should().Be("urn:hmz:error:test.failure");
        root.GetProperty("traceId").GetString().Should().Be("trace-contract-test");
        root.GetProperty("instance").GetString().Should().Be("/roles/42");
    }

    [Fact]
    public async Task ConcurrencyConflict_MapsToPreconditionFailed()
    {
        // Arrange
        var result = Result.Failure(CommonErrors.ConcurrencyPreconditionFailed);

        // Action
        using var response = await ExecuteAsync(
            result);

        // Assertion
        response.RootElement.GetProperty("status").GetInt32()
            .Should().Be(StatusCodes.Status412PreconditionFailed);
    }

    [Fact]
    public async Task ValidationErrors_PreserveFieldMessagesAndCodes()
    {
        // Arrange
        var result = Result.Failure(
            Error.Validation("role.name_required", "Name is required.", "name"),
            Error.Validation("role.code_required", "Code is required.", "code"));

        // Action
        using var response = await ExecuteAsync(result);
        var root = response.RootElement;

        // Assertion
        root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status400BadRequest);
        root.GetProperty("code").GetString().Should().Be(CommonErrorCodes.ValidationFailed);
        root.GetProperty("errors").GetProperty("name")[0].GetString()
            .Should().Be("Name is required.");
        root.GetProperty("errorCodes").GetProperty("code")[0].GetString()
            .Should().Be("role.code_required");
        root.GetProperty("type").GetString()
            .Should().Be($"urn:hmz:error:{CommonErrorCodes.ValidationFailed}");
    }

    [Fact]
    public async Task UnexpectedError_IsLoggedAndSanitized()
    {
        // Arrange
        var result = Result.Failure(
            Error.Unexpected("database.secret", "Server=prod;Password=secret"));

        var logs = new RecordingLoggerProvider();

        // Action
        using var response = await ExecuteAsync(
            result,
            configureServices: services =>
                services.AddSingleton<ILoggerProvider>(logs));
        var json = response.RootElement.GetRawText();

        // Assertion
        response.RootElement.GetProperty("status").GetInt32()
            .Should().Be(StatusCodes.Status500InternalServerError);
        response.RootElement.GetProperty("code").GetString()
            .Should().Be(CommonErrorCodes.InternalError);
        response.RootElement.GetProperty("detail").GetString()
            .Should().Be(CommonErrors.InternalError.Message);
        json.Should().NotContain("Password=secret");
        json.Should().NotContain("database.secret");
        logs.Messages.Should().Contain(message =>
            message.Contains("database.secret", StringComparison.Ordinal));
        logs.Messages.Should().NotContain(message =>
            message.Contains("Password=secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnauthorizedError_AddsBearerChallenge()
    {
        // Arrange
        string? challenge = null;
        var result = Result.Failure(Error.Unauthorized(
            "auth.required",
            "Authentication is required."));

        // Action
        using var response = await ExecuteAsync(
            result,
            inspectResponse: httpResponse =>
                challenge = httpResponse.Headers.WWWAuthenticate.ToString());

        // Assertion
        challenge.Should().Be("Bearer");
        response.RootElement.GetProperty("status").GetInt32()
            .Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task FrameworkGenerated500_IsSanitizedByTheGlobalProblemDetailsService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHmzProblemDetails();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            TraceIdentifier = "trace-framework-test"
        };
        context.Request.Path = "/framework-failure";
        context.Response.Body = new MemoryStream();

        var problemDetailsService = provider.GetRequiredService<IProblemDetailsService>();

        // Action
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Database token: secret-title",
                Detail = "Server=prod;Password=secret",
                Extensions =
                {
                    ["diagnostic"] = "ApiKey=secret-extension"
                }
            }
        });
        context.Response.Body.Position = 0;

        using var response = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        var json = response.RootElement.GetRawText();

        // Assertion
        response.RootElement.GetProperty("code").GetString()
            .Should().Be(CommonErrorCodes.InternalError);
        response.RootElement.GetProperty("detail").GetString()
            .Should().Be(CommonErrors.InternalError.Message);
        response.RootElement.GetProperty("traceId").GetString()
            .Should().Be("trace-framework-test");
        response.RootElement.GetProperty("title").GetString()
            .Should().Be("An unexpected error occurred");
        json.Should().NotContain("Password=secret");
        json.Should().NotContain("secret-title");
        json.Should().NotContain("secret-extension");
    }

    private static async Task<JsonDocument> ExecuteAsync(
        Hmz.Core.SharedKernel.Results.IResult result,
        Action<IServiceCollection>? configureServices = null,
        Action<HttpResponse>? inspectResponse = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHmzProblemDetails();
        configureServices?.Invoke(services);

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            TraceIdentifier = "trace-contract-test"
        };
        context.Request.Path = "/roles/42";
        context.Response.Body = new MemoryStream();

        await result.ToProblem(context).ExecuteAsync(context);
        inspectResponse?.Invoke(context.Response);
        context.Response.Body.Position = 0;

        return await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }
}