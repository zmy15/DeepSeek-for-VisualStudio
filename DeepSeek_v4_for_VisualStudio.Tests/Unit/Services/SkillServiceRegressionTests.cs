using Microsoft.Extensions.DependencyInjection;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class SkillServiceRegressionTests
{
    [Fact]
    public void SkillSignature_IsOrderIndependent_AndChangesWithContent()
    {
        var first = new List<SkillDefinition>
        {
            new() { Name = "a", Description = "A", Body = "body-a" },
            new() { Name = "b", Description = "B", Body = "body-b" },
        };
        var reordered = new List<SkillDefinition>
        {
            new() { Name = "b", Description = "B", Body = "body-b" },
            new() { Name = "a", Description = "A", Body = "body-a" },
        };

        string firstSignature = SkillService.BuildSkillsSignature(first);
        string reorderedSignature = SkillService.BuildSkillsSignature(reordered);

        reorderedSignature.Should().Be(firstSignature);

        first[0].Body = "changed";
        SkillService.BuildSkillsSignature(first).Should().NotBe(firstSignature);
    }

    [Fact]
    public void ServiceCollection_ResolvesCanonicalSkillServiceSingleton()
    {
        var services = new ServiceCollection();
        services.AddDeepSeekServices();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISkillService>().Should().BeSameAs(SkillService.Instance);
    }

    [Fact]
    public void ReadSkillResource_RejectsSiblingDirectoryTraversal()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "DeepSeekVS-SkillTests",
            Guid.NewGuid().ToString("N"));
        string skillRoot = Path.Combine(testRoot, "skill");
        string siblingRoot = Path.Combine(testRoot, "skill-secret");
        Directory.CreateDirectory(skillRoot);
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(Path.Combine(siblingRoot, "secret.txt"), "secret");

        try
        {
            var skill = new SkillDefinition { RootDirectory = skillRoot };
            string relativePath = Path.Combine("..", "skill-secret", "secret.txt");

            SkillService.Instance.ReadSkillResource(skill, relativePath).Should().BeNull();
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
