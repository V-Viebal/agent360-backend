using Hmz.Core.SharedKernel.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class EntityTagHttpTests
{
    [Theory]
    [InlineData(0u, "\"0\"")]
    [InlineData(42u, "\"42\"")]
    [InlineData(uint.MaxValue, "\"4294967295\"")]
    public void FormatStrong_UsesCanonicalQuotedDecimalValue(
        uint rowVersion,
        string expectedTag)
    {
        // Arrange
        // Theory data supplies the row version and expected wire value.

        // Action
        var result = EntityTagHttp.FormatStrong(rowVersion);

        // Assertion
        result.Should().Be(expectedTag);
    }

    [Theory]
    [InlineData(0u, "\"0\"")]
    [InlineData(1u, "\"1\"")]
    [InlineData(uint.MaxValue, "\"4294967295\"")]
    public void WriteAndReadStrong_RoundTripsOpaqueCanonicalTag(
        uint rowVersion,
        string expectedTag)
    {
        // Arrange
        var writeContext = new DefaultHttpContext();

        // Action
        EntityTagHttp.WriteStrong(writeContext.Response, rowVersion);
        var readContext = new DefaultHttpContext();
        readContext.Request.Headers.IfMatch = expectedTag;
        var parseResult = EntityTagHttp.TryReadStrongIfMatch(
            readContext.Request,
            out var parsedRowVersion);

        // Assertion
        writeContext.Response.Headers.ETag.ToString().Should().Be(expectedTag);
        parseResult.Should().Be(EntityTagParseResult.Success);
        parsedRowVersion.Should().Be(rowVersion);
    }

    [Fact]
    public void TryReadStrongIfMatch_MissingHeader_ReturnsMissing()
    {
        // Arrange
        var context = new DefaultHttpContext();

        // Action
        var result = EntityTagHttp.TryReadStrongIfMatch(
            context.Request,
            out _);

        // Assertion
        result.Should().Be(EntityTagParseResult.Missing);
    }

    [Theory]
    [InlineData("W/\"1\"")]
    [InlineData("*")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"01\"")]
    public void TryReadStrongIfMatch_NonCanonicalOrUnsupportedHeader_ReturnsInvalid(
        string headerValue)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers.IfMatch = headerValue;

        // Action
        var result = EntityTagHttp.TryReadStrongIfMatch(
            context.Request,
            out _);

        // Assertion
        result.Should().Be(EntityTagParseResult.Invalid);
    }

    [Fact]
    public void EvaluateIfNoneMatch_MissingHeader_ReturnsMissing()
    {
        // Arrange
        var context = new DefaultHttpContext();

        // Action
        var result = EntityTagHttp.EvaluateIfNoneMatch(
            context.Request,
            currentRowVersion: 1);

        // Assertion
        result.Should().Be(IfNoneMatchResult.Missing);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("W/\"1\"")]
    [InlineData("\"other\", \"1\"")]
    [InlineData("*")]
    public void EvaluateIfNoneMatch_MatchingValidator_ReturnsMatch(
        string headerValue)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["If-None-Match"] = headerValue;

        // Action
        var result = EntityTagHttp.EvaluateIfNoneMatch(
            context.Request,
            currentRowVersion: 1);

        // Assertion
        result.Should().Be(IfNoneMatchResult.Match);
    }

    [Fact]
    public void EvaluateIfNoneMatch_NonMatchingValidator_ReturnsNoMatch()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["If-None-Match"] = "\"2\"";

        // Action
        var result = EntityTagHttp.EvaluateIfNoneMatch(
            context.Request,
            currentRowVersion: 1);

        // Assertion
        result.Should().Be(IfNoneMatchResult.NoMatch);
    }

    [Theory]
    [InlineData("W/")]
    [InlineData("\"unterminated")]
    [InlineData("1")]
    public void EvaluateIfNoneMatch_InvalidHeader_ReturnsInvalid(
        string headerValue)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["If-None-Match"] = headerValue;

        // Action
        var result = EntityTagHttp.EvaluateIfNoneMatch(
            context.Request,
            currentRowVersion: 2);

        // Assertion
        result.Should().Be(IfNoneMatchResult.Invalid);
    }
}