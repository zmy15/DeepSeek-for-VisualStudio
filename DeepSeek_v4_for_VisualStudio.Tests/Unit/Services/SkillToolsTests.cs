using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using Moq;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class SkillToolsTests
{
    [Fact]
    public void ListSkillsTool_Definition_UsesExpectedName()
    {
        var tool = new ListSkillsTool(Mock.Of<ISkillService>());

        var definition = tool.GetDefinition();

        definition.Function.Name.Should().Be("list_skills");
        definition.Function.Description.Should().NotBeNullOrWhiteSpace();
        definition.Function.Parameters.Should().NotBeNull();
    }

    [Fact]
    public async Task ListSkillsTool_ExecuteAsync_ListsAllSkillsWithUsage()
    {
        var discovery = new SkillDiscoveryResult
        {
            Skills = new List<SkillDefinition>
            {
                new()
                {
                    Name = "project-skill",
                    Description = "Project workflow",
                    Source = SkillSource.Project,
                },
                new()
                {
                    Name = "user-only",
                    Description = "Only the user may invoke this.",
                    Source = SkillSource.User,
                    DisableModelInvocation = true,
                },
                new()
                {
                    Name = "model-only",
                    Description = "Only the model may load this.",
                    Source = SkillSource.BuiltIn,
                    UserInvocable = false,
                },
                new()
                {
                    Name = "always-active",
                    Description = "Injected every turn.",
                    Source = SkillSource.User,
                    AlwaysInject = true,
                },
            },
        };
        var skillService = new Mock<ISkillService>();
        skillService
            .Setup(service => service.DiscoverSkillsAsync(It.IsAny<string?>(), false))
            .ReturnsAsync(discovery);
        var tool = new ListSkillsTool(skillService.Object);

        string result = await tool.ExecuteAsync(
            new Dictionary<string, JsonElement>(),
            @"F:\repo");

        result.Should().Contain("project-skill");
        result.Should().Contain("user-only");
        result.Should().Contain("model-only");
        result.Should().Contain("always-active");
        result.Should().Contain("Project workflow");
        result.Should().Contain(LocalizationService.Instance["tool.listSkills.usageModelAndUser"]);
        result.Should().Contain(LocalizationService.Instance["tool.listSkills.usageUserOnly"]);
        result.Should().Contain(LocalizationService.Instance["tool.listSkills.usageModelOnly"]);
        result.Should().Contain(LocalizationService.Instance["tool.listSkills.usageAlwaysInject"]);
        skillService.Verify(
            service => service.DiscoverSkillsAsync(@"F:\repo", false),
            Times.Once);
    }

    [Fact]
    public async Task ListSkillsTool_ExecuteAsync_NoSkills_ReturnsEmptyMessage()
    {
        var skillService = new Mock<ISkillService>();
        skillService
            .Setup(service => service.DiscoverSkillsAsync(It.IsAny<string?>(), false))
            .ReturnsAsync(new SkillDiscoveryResult());
        var tool = new ListSkillsTool(skillService.Object);

        string result = await tool.ExecuteAsync(
            new Dictionary<string, JsonElement>(),
            null);

        result.Should().Contain(LocalizationService.Instance["tool.listSkills.noSkills"]);
    }
}
