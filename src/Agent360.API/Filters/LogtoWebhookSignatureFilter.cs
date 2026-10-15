using Hmz.Core.SharedKernel.Security;
using Agent360.API.Options;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Agent360.API.Filters;

public class LogtoWebhookSignatureFilter(IOptionsMonitor<LogtoOptions> logtoOpts) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;

        if (!request.Headers.TryGetValue(Constants.LogtoDefaults.WebhookSignatureHeaderKey, out var signatureValues)
            || string.IsNullOrWhiteSpace(signatureValues.FirstOrDefault()))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Missing webhook signature");
        }

        var receivedSignature = signatureValues.First() ?? string.Empty;

        request.EnableBuffering();
        using var reader = new StreamReader(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.HttpContext.RequestAborted);
        request.Body.Position = 0; // Rewind so model binding still works

        var signatureKey = logtoOpts.CurrentValue.Webhooks.SignUpSigningKey
            ?? throw new InvalidOperationException("Logto:WebhookSigningKey is not configured yet.");

        var keyBytes = Encoding.UTF8.GetBytes(signatureKey);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var computedHash = HMACSHA256.HashData(keyBytes, bodyBytes);
        var computedDigest = Convert.ToHexString(computedHash).ToLowerInvariant();

        if (!FixedTimeSecretComparer.Equals(computedDigest, receivedSignature))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid webhook signature");
        }

        return await next(context);
    }
}