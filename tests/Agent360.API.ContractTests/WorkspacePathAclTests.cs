using Agent360.API.Features.Agents.Security;
using Xunit;

namespace Agent360.API.ContractTests;

public sealed class WorkspacePathAclTests
{
    [Theory]
    [InlineData(".env", false)]
    [InlineData("config/.env.production", false)]
    [InlineData("/app/working.secret/ssh/id_rsa", false)]
    [InlineData(".git/config", false)]
    [InlineData("src/../.env", false)]
    [InlineData("src/components/Navbar.tsx", true)]
    [InlineData("docs/README.md", true)]
    [InlineData("AGENTS.md", true)]
    public void WorkspacePathAcl_FiltersCorrectly(string path, bool expectedAllowed)
    {
        var result = WorkspacePathAcl.IsPathAllowed(path);
        Assert.Equal(expectedAllowed, result);
    }
}
