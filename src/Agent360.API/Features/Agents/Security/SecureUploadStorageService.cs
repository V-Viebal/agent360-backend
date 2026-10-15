using System.Security.Cryptography;
using Agent360.API.Features.Agents.Models;

namespace Agent360.API.Features.Agents.Security;

public static class SecureUploadStorageService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg",
        ".pdf", ".txt", ".md", ".json", ".csv", ".yaml", ".yml",
        ".py", ".ts", ".tsx", ".js", ".cs", ".html", ".css"
    };

    private const long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB

    public static async Task<(bool Success, string? Error, FileUploadResponse? Response)> StoreFileAsync(
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

        // Virtual URI hides physical disk path from external clients & QwenPaw
        var virtualUri = $"virtual://workspace/uploads/{fileId}/{sanitizedFilename}{ext}";

        var response = new FileUploadResponse(
            FileId: fileId,
            Filename: $"{sanitizedFilename}{ext}",
            VirtualUri: virtualUri,
            SizeBytes: file.Length,
            ContentType: file.ContentType,
            UploadedAt: DateTime.UtcNow);

        return (true, null, response);
    }
}
