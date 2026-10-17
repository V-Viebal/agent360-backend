using Agent360.API.Features.Agents.Models;

namespace Agent360.API.Features.Agents.Services;

public interface IBrowserCdpService
{
    Task<BrowserCdpStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BrowserCdpTargetDto>> GetTargetsAsync(CancellationToken cancellationToken = default);
    Task<byte[]> CaptureScreenshotAsync(string? targetId = null, CancellationToken cancellationToken = default);
}
