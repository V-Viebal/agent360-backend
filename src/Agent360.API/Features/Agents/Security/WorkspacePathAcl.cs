using Agent360.API.Features.Agents.Models;

namespace Agent360.API.Features.Agents.Security;

public static class WorkspacePathAcl
{
    private static readonly HashSet<string> DeniedExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        ".env.local",
        ".env.production",
        ".env.staging",
        "id_rsa",
        "id_ed25519",
        "credentials.json",
        "credentials.yaml",
        "history.db",
        "access_control.json"
    };

    private static readonly string[] DeniedPathSegments =
    [
        "/.git/",
        "/.agent/mcps/",
        "/working.secret/",
        "/ssh/",
        "/credentials/",
        "/.credentials/"
    ];

    // Internal system directories and state folders inside QwenPaw agent workspaces
    private static readonly HashSet<string> InternalWorkspaceSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "checkpoints",
        "sessions",
        "mem_agent",
        "mem_metadata",
        "mem_session",
        "state",
        "tool_result",
        "tool_results",
        "jobs_history",
        "embedding_cache",
        "file_store",
        "backup",
        "dialog",
        "drivers",
        "tmp",
        "skills",
        "memory",
        "browser",
        "node_modules",
        "dist",
        "build",
        "frames",
        "inspect",
        "cuts",
        "preview",
        "__pycache__",
        ".git",
        ".agent",
        ".tmp",
        ".credentials",
        ".sync-backups",
        ".skill-backups",
        ".playwright-mcp"
    };

    // Internal configuration and system prompt files at workspace root
    private static readonly HashSet<string> InternalRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent.json",
        "chats.json",
        "credentials.yaml",
        "credentials.json",
        "history.db",
        "jobs.json",
        "skill.json",
        "access_control.json",
        "copaw_file_metadata.json",
        "memory_file_metadata.json",
        "AGENTS.md",
        "BOOTSTRAP.md",
        "HEARTBEAT.md",
        "IDENTITY.md",
        "MEMORY.md",
        "PROFILE.md",
        "SOUL.md",
        "package-lock.json",
        "bun.lock",
        "yarn.lock",
        "pnpm-lock.yaml"
    };

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "mdx", "html", "htm"
    };

    private static readonly HashSet<string> AppCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "py", "ts", "tsx", "js", "jsx", "sh", "bash", "cs", "go", "rs", "java", "cpp", "c", "h", "css", "scss", "less", "sql", "vue", "svelte"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "webp", "svg", "ico", "bmp", "avif", "heic", "heif"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "mov", "avi", "mp3", "wav", "ogg", "m4a", "aac", "flac"
    };

    private static readonly HashSet<string> DocumentDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "xlsx", "xls", "xlsm", "docx", "doc", "pptx", "ppt", "csv", "tsv", "txt", "json", "yaml", "yml", "xml", "toml", "ini", "zip", "rar", "7z", "tar", "gz", "tgs"
    };

    public static bool IsPathAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();

        // Prevent path traversal
        if (normalized.Contains(".."))
        {
            return false;
        }

        // Check file name
        var fileName = Path.GetFileName(normalized);
        if (DeniedExactNames.Contains(fileName) || fileName.Contains(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check path segments
        var padded = "/" + normalized.Trim('/') + "/";
        foreach (var segment in DeniedPathSegments)
        {
            if (padded.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a workspace path is a user-visible Library artifact (Page, File, App, Image, Media)
    /// rather than an internal QwenPaw engine file, backup, or cache artifact.
    /// </summary>
    public static bool IsLibraryArtifactPath(string? path)
    {
        if (!IsPathAllowed(path))
        {
            return false;
        }

        var normalized = path!.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var seg in segments)
        {
            if (seg.StartsWith('.') ||
                InternalWorkspaceSegments.Contains(seg) ||
                seg.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
                seg.Contains("obsolete", StringComparison.OrdinalIgnoreCase) ||
                seg.StartsWith("tmp-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var fileName = segments[^1];
        if (segments.Length == 1 && InternalRootFiles.Contains(fileName))
        {
            return false;
        }

        if (InternalRootFiles.Contains(fileName) && (fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) || fileName.Equals("bun.lock", StringComparison.OrdinalIgnoreCase) || fileName.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    public static string ExtractExtension(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return "";
        }

        var ext = Path.GetExtension(nameOrPath).TrimStart('.').ToLowerInvariant();
        return ext;
    }

    /// <summary>
    /// Classifies an entry into one of the canonical Library categories:
    /// pages, files, apps, images, media, or directory.
    /// </summary>
    public static (string Category, string PreviewKind, string MimeType) ClassifyArtifact(string? pathOrName, string? kind = "file")
    {
        if (string.Equals(kind, "directory", StringComparison.OrdinalIgnoreCase))
        {
            return ("directory", "directory", "inode/directory");
        }

        var ext = ExtractExtension(pathOrName);
        if (PageExtensions.Contains(ext))
        {
            var preview = ext is "html" or "htm" ? "html" : "markdown";
            var mime = ext is "html" or "htm" ? "text/html" : "text/markdown";
            return ("pages", preview, mime);
        }

        if (AppCodeExtensions.Contains(ext))
        {
            return ("apps", "code", GetMimeType(ext));
        }

        if (ImageExtensions.Contains(ext))
        {
            return ("images", "image", GetMimeType(ext));
        }

        if (MediaExtensions.Contains(ext))
        {
            var preview = ext is "mp4" or "webm" or "mov" or "avi" ? "video" : "audio";
            return ("media", preview, GetMimeType(ext));
        }

        if (ext == "pdf")
        {
            return ("files", "pdf", "application/pdf");
        }

        if (ext is "csv" or "tsv")
        {
            return ("files", "csv", ext == "tsv" ? "text/tab-separated-values" : "text/csv");
        }

        if (ext is "xlsx" or "xls" or "xlsm")
        {
            return ("files", "spreadsheet", GetMimeType(ext));
        }

        if (ext is "docx" or "doc")
        {
            return ("files", "document", GetMimeType(ext));
        }

        if (ext is "txt" or "json" or "yaml" or "yml" or "xml" or "toml" or "ini" or "log")
        {
            return ("files", "text", GetMimeType(ext));
        }

        return ("files", "binary", GetMimeType(ext));
    }

    public static string GetMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        "ico" => "image/x-icon",
        "bmp" => "image/bmp",
        "avif" => "image/avif",
        "heic" => "image/heic",
        "heif" => "image/heif",
        "pdf" => "application/pdf",
        "csv" => "text/csv",
        "tsv" => "text/tab-separated-values",
        "md" or "mdx" => "text/markdown",
        "html" or "htm" => "text/html",
        "txt" or "log" or "ini" => "text/plain",
        "json" => "application/json",
        "yaml" or "yml" => "application/x-yaml",
        "xml" => "application/xml",
        "py" => "text/x-python",
        "js" or "jsx" => "text/javascript",
        "ts" or "tsx" => "text/typescript",
        "css" => "text/css",
        "sh" or "bash" => "text/x-shellscript",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "xlsm" => "application/vnd.ms-excel.sheet.macroEnabled.12",
        "xls" => "application/vnd.ms-excel",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "doc" => "application/msword",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "ppt" => "application/vnd.ms-powerpoint",
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" => "audio/ogg",
        "m4a" => "audio/mp4",
        "zip" => "application/zip",
        "gz" => "application/gzip",
        _ => "application/octet-stream"
    };

    public static QwenPawWorkspaceEntryDto EnrichEntry(QwenPawWorkspaceEntryDto entry)
    {
        var ext = ExtractExtension(entry.Path ?? entry.Name);
        var (category, previewKind, mimeType) = ClassifyArtifact(entry.Path ?? entry.Name, entry.Kind);
        var cleanName = Path.GetFileName((entry.Path ?? entry.Name).Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            cleanName = entry.Name;
        }

        return entry with
        {
            Name = cleanName,
            PreviewKind = previewKind,
            Category = category,
            Extension = string.IsNullOrEmpty(ext) ? null : ext,
            MimeType = mimeType
        };
    }
}
