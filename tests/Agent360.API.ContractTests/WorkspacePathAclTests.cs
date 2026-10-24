using Agent360.API.Features.Agents.Models;
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
    [InlineData("agent.json.bak.123", false)]
    [InlineData("credentials.yaml", false)]
    [InlineData("src/components/Navbar.tsx", true)]
    [InlineData("docs/README.md", true)]
    [InlineData("AGENTS.md", true)]
    public void WorkspacePathAcl_FiltersCorrectly(string path, bool expectedAllowed)
    {
        var result = WorkspacePathAcl.IsPathAllowed(path);
        Assert.Equal(expectedAllowed, result);
    }

    [Theory]
    [InlineData("AGENTS.md", false)]
    [InlineData("MEMORY.md", false)]
    [InlineData("agent.json", false)]
    [InlineData("chats.json", false)]
    [InlineData("checkpoints/cp1.json", false)]
    [InlineData("sessions/s1/turn.json", false)]
    [InlineData("skills/erpnext/SKILL.md", false)]
    [InlineData("media/dongque-video2/frames/0001.jpg", false)]
    [InlineData("projects/gm-nextcloud.obsolete-local-do-not-use/README.md", false)]
    [InlineData("Bao_gia_DongQue_Thang10_2026_chot.xlsx", true)]
    [InlineData("digest/procedure/worktime-leave-net-hours.md", true)]
    [InlineData("media/0a50d66e88884e7f94cf392610b16234_test.pdf", true)]
    [InlineData("create_tender_workbook.py", true)]
    [InlineData("meo-dang-yeu.svg", true)]
    public void IsLibraryArtifactPath_FiltersOutEngineNoise_AndKeepsUserArtifacts(string path, bool expectedIsArtifact)
    {
        Assert.Equal(expectedIsArtifact, WorkspacePathAcl.IsLibraryArtifactPath(path));
    }

    [Theory]
    [InlineData("digest/procedure/guide.md", "pages", "markdown")]
    [InlineData("incoming/index.html", "pages", "html")]
    [InlineData("create_tender_workbook.py", "apps", "code")]
    [InlineData("src/App.tsx", "apps", "code")]
    [InlineData("media/report.pdf", "files", "pdf")]
    [InlineData("Bao_gia.xlsx", "files", "spreadsheet")]
    [InlineData("Hop_dong.docx", "files", "document")]
    [InlineData("media/slide.png", "images", "image")]
    [InlineData("media/promo.mp4", "media", "video")]
    public void EnrichEntry_ClassifiesFormatsAndCategories(string path, string expectedCategory, string expectedPreviewKind)
    {
        var raw = new QwenPawWorkspaceEntryDto(path, path, "file", null, 1024, "2026-10-22T10:00:00Z");
        var enriched = WorkspacePathAcl.EnrichEntry(raw);

        Assert.Equal(expectedCategory, enriched.Category);
        Assert.Equal(expectedPreviewKind, enriched.PreviewKind);
        Assert.False(string.IsNullOrWhiteSpace(enriched.Extension));
        Assert.DoesNotContain("/", enriched.Name);
    }
}
