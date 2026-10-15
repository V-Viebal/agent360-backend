namespace Agent360.API.Features.Agents.Workspaces;

public sealed record WorkspaceFileNode(
    string Name,
    string Path,
    string Type,
    long Size,
    DateTimeOffset ModifiedAt);

public sealed record WorkspaceFileContentResponse(
    string Path,
    string Content,
    long Size,
    string Encoding = "utf-8");
