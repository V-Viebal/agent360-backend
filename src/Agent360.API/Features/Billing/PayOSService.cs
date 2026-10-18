using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent360.API.Features.Billing;

public sealed class PayOSService(IConfiguration config, HttpClient httpClient, ILogger<PayOSService> logger)
{
    private string ClientId => config["PayOS:ClientId"] ?? "1bd71bb3-73c9-4188-a1f6-ef10c7b65240";
    private string ApiKey => config["PayOS:ApiKey"] ?? "a1ddbdd0-1835-49a9-ae5e-071195172d7e";
    private string ChecksumKey => config["PayOS:ChecksumKey"] ?? "7df6cf9f0c1a396211193593049742211286979073e03dfe02602fe8f0350cf3";

    public async Task<(string? CheckoutUrl, string? PaymentLinkId, string? Error)> CreatePaymentLinkAsync(
        long orderCode,
        int amount,
        string description,
        string returnUrl,
        string cancelUrl,
        CancellationToken ct = default)
    {
        try
        {
            var signData = $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";
            var signature = HmacSha256(ChecksumKey, signData);

            var payload = new
            {
                orderCode,
                amount,
                description,
                returnUrl,
                cancelUrl,
                signature
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api-merchant.payos.vn/v2/payment-requests")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            request.Headers.Add("x-client-id", ClientId);
            request.Headers.Add("x-api-key", ApiKey);

            var res = await httpClient.SendAsync(request, ct);
            var resBody = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogError("PayOS link creation failed: {Body}", resBody);
                return (null, null, "PayOS gateway returned error");
            }

            using var doc = JsonDocument.Parse(resBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("checkoutUrl", out var checkoutUrl))
            {
                var linkId = data.TryGetProperty("paymentLinkId", out var plId) ? plId.GetString() : null;
                return (checkoutUrl.GetString(), linkId, null);
            }

            return (null, null, "Unexpected response from PayOS");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to PayOS gateway");
            return (null, null, ex.Message);
        }
    }

    private static string HmacSha256(string key, string data)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
