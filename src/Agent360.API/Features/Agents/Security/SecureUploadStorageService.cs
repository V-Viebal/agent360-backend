using System.Security.Cryptography;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Options;
using Microsoft.Extensions.Options;

namespace Agent360.API.Features.Agents.Security;

public class SecureUploadStorageService(
    IOptionsMonitor<StorageOptions> storageOpts,
    IHttpClientFactory httpClientFactory,
    ILogger<SecureUploadStorageService> logger)
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico",
        ".pdf", ".txt", ".md", ".mdx", ".json", ".csv", ".tsv", ".yaml", ".yml", ".xml", ".toml", ".ini", ".log",
        ".docx", ".doc", ".xlsx", ".xls", ".xlsm", ".pptx", ".ppt",
        ".zip", ".tar", ".gz", ".7z", ".rar",
        ".mp3", ".wav", ".ogg", ".m4a", ".mp4", ".webm", ".mov",
        ".py", ".ts", ".tsx", ".js", ".jsx", ".cs", ".go", ".rs", ".java", ".cpp", ".c", ".h", ".sh", ".sql", ".html", ".htm", ".css", ".scss"
    };

    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB

    public async Task<(bool Success, string? Error, FileUploadResponse? Response)> StoreFileAsync(
        IFormFile file,
        string uploadDirectoryRoot,
        CancellationToken ct = default)
    {
        if (file.Length == 0)
        {
            return (false, "File cannot be empty.", null);
        }

        if (file.Length > MaxFileSizeBytes)
        {
            return (false, "File size exceeds maximum allowed limit of 25MB.", null);
        }

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
        {
            return (false, $"File extension '{ext}' is not permitted by upload security policy.", null);
        }

        var fileId = "upl_" + Guid.NewGuid().ToString("N");
        var sanitizedFilename = Path.GetFileNameWithoutExtension(file.FileName)
            .Replace(" ", "_")
            .Replace("..", "");
        
        sanitizedFilename = string.Concat(sanitizedFilename.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        if (string.IsNullOrWhiteSpace(sanitizedFilename))
        {
            sanitizedFilename = "attachment";
        }

        var storageFilename = $"{fileId}_{sanitizedFilename}{ext}";
        var targetDir = Path.Combine(uploadDirectoryRoot, ".tmp", "agent360-uploads");
        Directory.CreateDirectory(targetDir);

        var destinationPath = Path.Combine(targetDir, storageFilename);

        await using (var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await file.CopyToAsync(stream, ct);
        }

        var currentConfig = storageOpts.CurrentValue;
        string publicUri = $"virtual://workspace/uploads/{fileId}/{sanitizedFilename}{ext}";

        // If MinIO S3 is configured, push the payload to MinIO directly
        if (currentConfig.Provider.Equals("Minio", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentConfig.ServerUrl))
        {
            try
            {
                var s3Endpoint = currentConfig.ServerUrl.TrimEnd('/');
                var s3Url = $"{s3Endpoint}/{currentConfig.BucketName}/{storageFilename}";
                var client = httpClientFactory.CreateClient("MinioStorageClient");
                
                await using var uploadStream = File.OpenRead(destinationPath);
                using var content = new StreamContent(uploadStream);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

                var s3Request = new HttpRequestMessage(HttpMethod.Put, s3Url) { Content = content };
                var s3Response = await client.SendAsync(s3Request, ct);

                if (s3Response.IsSuccessStatusCode)
                {
                    publicUri = s3Url;
                    logger.LogInformation("Successfully uploaded {FileId} to MinIO S3: {Url}", fileId, s3Url);
                }
                else
                {
                    logger.LogWarning("MinIO S3 upload returned {Status}; fallback to local virtual URI", s3Response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MinIO S3 sync failed for {FileId}; fallback to local virtual URI", fileId);
            }
        }

        var resolvedMime = file.ContentType ?? "application/octet-stream";
        var response = new FileUploadResponse(
            FileId: fileId,
            Filename: $"{sanitizedFilename}{ext}",
            VirtualUri: publicUri,
            SizeBytes: file.Length,
            ContentType: resolvedMime,
            UploadedAt: DateTime.UtcNow)
        {
            Url = publicUri,
            FileName = file.FileName ?? $"{sanitizedFilename}{ext}",
            Size = file.Length,
            MimeType = resolvedMime
        };

        return (true, null, response);
    }
}
