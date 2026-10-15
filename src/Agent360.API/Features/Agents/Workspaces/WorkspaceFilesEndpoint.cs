using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Agent360.API.Features.Agents.Workspaces;

public sealed class WorkspaceFilesEndpoint : IEndpoint
{
    private static readonly string RootWorkspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. List files in workspace (Path ACL protected)
        routeBuilder.MapGet(
            "workspaces/files",
            (
                [FromQuery] string? relativePath) =>
            {
                var targetRel = string.IsNullOrWhiteSpace(relativePath) ? "" : relativePath.TrimStart('/', '\\');
                var fullTarget = Path.GetFullPath(Path.Combine(RootWorkspace, targetRel));

                // Strict Path ACL: Prevent path traversal
                if (!fullTarget.StartsWith(RootWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { message = "Access denied: Path traversal outside workspace root." });
                }

                if (!Directory.Exists(fullTarget))
                {
                    return Results.NotFound(new { message = "Directory not found." });
                }

                var entries = new List<WorkspaceFileNode>();
                var dirInfo = new DirectoryInfo(fullTarget);

                foreach (var dir in dirInfo.GetDirectories().Where(d => !d.Name.StartsWith(".")))
                {
                    entries.Add(new WorkspaceFileNode(
                        Name: dir.Name,
                        Path: Path.GetRelativePath(RootWorkspace, dir.FullName).Replace('\\', '/'),
                        Type: "directory",
                        Size: 0,
                        ModifiedAt: dir.LastWriteTimeUtc));
                }

                foreach (var file in dirInfo.GetFiles().Where(f => !f.Name.StartsWith(".")))
                {
                    entries.Add(new WorkspaceFileNode(
                        Name: file.Name,
                        Path: Path.GetRelativePath(RootWorkspace, file.FullName).Replace('\\', '/'),
                        Type: "file",
                        Size: file.Length,
                        ModifiedAt: file.LastWriteTimeUtc));
                }

                return Results.Ok(entries);
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("ListWorkspaceFiles")
            .WithTags(EndpointTags.Agents)
            .WithSummary("List directories and files in workspace with Path ACL")
            .AllowAnonymous();

        // 2. Read file content safely
        routeBuilder.MapGet(
            "workspaces/content",
            async (
                [FromQuery] string filePath,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return Results.BadRequest(new { message = "filePath is required" });
                }

                var cleanRel = filePath.TrimStart('/', '\\');
                var fullTarget = Path.GetFullPath(Path.Combine(RootWorkspace, cleanRel));

                // Strict Path ACL
                if (!fullTarget.StartsWith(RootWorkspace, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { message = "Access denied: Path traversal prohibited." });
                }

                if (!File.Exists(fullTarget))
                {
                    return Results.NotFound(new { message = "File not found." });
                }

                var fileInfo = new FileInfo(fullTarget);
                if (fileInfo.Length > 2 * 1024 * 1024) // 2MB limit for text read
                {
                    return Results.BadRequest(new { message = "File exceeds maximum preview size of 2MB" });
                }

                var content = await File.ReadAllTextAsync(fullTarget, cancellationToken);
                return Results.Ok(new WorkspaceFileContentResponse(
                    Path: cleanRel.Replace('\\', '/'),
                    Content: content,
                    Size: fileInfo.Length));
            })
            .MapToApiVersion(ApiVersion.Default)
            .WithName("GetWorkspaceFileContent")
            .WithTags(EndpointTags.Agents)
            .WithSummary("Read safe content of a workspace file")
            .AllowAnonymous();
    }
}
