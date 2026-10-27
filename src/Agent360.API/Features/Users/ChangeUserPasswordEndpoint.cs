using Asp.Versioning;
using Hmz.Core.SharedKernel.Identity.Abstractions;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using System.Text.Json.Nodes;

namespace Agent360.API.Features.Users;

public sealed record ChangeMyPasswordRequest(string NewPassword);

public sealed class ChangeUserPasswordEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // Check if current user has password set
        routeBuilder.MapGet(
            "users/me/password",
            async Task<IResult> (
                ICurrentUser currentUser,
                IHttpClientFactory httpClientFactory,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
                {
                    return Results.Unauthorized();
                }

                var endpoint = "https://auth-skyer.hmz.one";
                var m2mId = "skyer-m2m-backend";
                var m2mSecret = "7pPJ85w2sM3l7CtgxTVZdGz7WEtvBb9P";

                var httpClient = httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(15);

                // 1. Acquire Logto Management API M2M token
                var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/oidc/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = m2mId,
                        ["client_secret"] = m2mSecret,
                        ["resource"] = "https://default.logto.app/api",
                        ["scope"] = "all"
                    })
                };
                tokenReq.Headers.Add("User-Agent", "Agent360/1.0");

                var tokenResp = await httpClient.SendAsync(tokenReq, ct);
                if (!tokenResp.IsSuccessStatusCode)
                {
                    return Results.Problem($"Không thể xác thực với cổng định danh Skyer ({tokenResp.StatusCode}).", statusCode: StatusCodes.Status502BadGateway);
                }

                var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonObject>(ct);
                var mgmtToken = tokenJson?["access_token"]?.ToString();
                if (string.IsNullOrWhiteSpace(mgmtToken))
                {
                    return Results.Problem("Không lấy được token quản trị từ cổng định danh Skyer.", statusCode: StatusCodes.Status502BadGateway);
                }

                // 2. Query user has-password status from Logto
                var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/api/users/{Uri.EscapeDataString(currentUser.ExternalUserId)}/has-password");
                statusReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mgmtToken);
                statusReq.Headers.Add("User-Agent", "Agent360/1.0");

                var statusResp = await httpClient.SendAsync(statusReq, ct);
                if (statusResp.IsSuccessStatusCode)
                {
                    var statusJson = await statusResp.Content.ReadFromJsonAsync<JsonObject>(ct);
                    var hasPassword = statusJson?["hasPassword"]?.GetValue<bool>() ?? false;
                    return Results.Ok(new { hasPassword });
                }

                return Results.Ok(new { hasPassword = false });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetCurrentUserPasswordStatus")
            .WithTags(EndpointTags.Users)
            .WithSummary("Check if the current authenticated user has set a password in Logto")
            .RequireAuthorization();

        // Change or set user password
        routeBuilder.MapPost(
            "users/me/password",
            async Task<IResult> (
                ChangeMyPasswordRequest req,
                ICurrentUser currentUser,
                IHttpClientFactory httpClientFactory,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
                {
                    return Results.BadRequest(new { error = "Mật khẩu phải có độ dài tối thiểu 8 ký tự!" });
                }

                var endpoint = "https://auth-skyer.hmz.one";
                var m2mId = "skyer-m2m-backend";
                var m2mSecret = "7pPJ85w2sM3l7CtgxTVZdGz7WEtvBb9P";

                var httpClient = httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(15);

                // 1. Acquire Logto Management API M2M token
                var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/oidc/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = m2mId,
                        ["client_secret"] = m2mSecret,
                        ["resource"] = "https://default.logto.app/api",
                        ["scope"] = "all"
                    })
                };
                tokenReq.Headers.Add("User-Agent", "Agent360/1.0");

                var tokenResp = await httpClient.SendAsync(tokenReq, ct);
                if (!tokenResp.IsSuccessStatusCode)
                {
                    return Results.Problem($"Không thể xác thực với cổng định danh Skyer ({tokenResp.StatusCode}).", statusCode: StatusCodes.Status502BadGateway);
                }

                var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonObject>(ct);
                var mgmtToken = tokenJson?["access_token"]?.ToString();
                if (string.IsNullOrWhiteSpace(mgmtToken))
                {
                    return Results.Problem("Không lấy được token quản trị từ cổng định danh Skyer.", statusCode: StatusCodes.Status502BadGateway);
                }

                // 2. Patch user password in Logto
                var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{endpoint}/api/users/{Uri.EscapeDataString(currentUser.ExternalUserId)}/password")
                {
                    Content = JsonContent.Create(new { password = req.NewPassword })
                };
                patchReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mgmtToken);
                patchReq.Headers.Add("User-Agent", "Agent360/1.0");

                var patchResp = await httpClient.SendAsync(patchReq, ct);
                if (!patchResp.IsSuccessStatusCode)
                {
                    var errBody = await patchResp.Content.ReadAsStringAsync(ct);
                    string errMessage = "Không thể cập nhật mật khẩu. Vui lòng chọn mật khẩu mạnh hơn (gồm chữ hoa, chữ thường, số hoặc ký tự đặc biệt).";
                    try
                    {
                        var errJson = System.Text.Json.Nodes.JsonNode.Parse(errBody)?.AsObject();
                        var msg = errJson?["message"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            errMessage = msg;
                        }
                    }
                    catch { }

                    return Results.BadRequest(new { error = errMessage, details = errBody });
                }

                return Results.Ok(new { success = true, message = "Đã cập nhật mật khẩu thành công!" });
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ChangeCurrentUserPassword")
            .WithTags(EndpointTags.Users)
            .WithSummary("Update the current authenticated user's password in Logto")
            .RequireAuthorization();
    }
}
