using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services
{
    public class GetCurrentTimeToolTests
    {
        [Fact]
        public void Definition_UsesExpectedToolNameAndEmptyArguments()
        {
            var tool = new GetCurrentTimeTool();

            var definition = tool.GetDefinition();

            definition.Function.Name.Should().Be("get_current_time");
            definition.Function.Description.Should().NotBeNullOrWhiteSpace();
            tool.GetDisplayText(new Dictionary<string, JsonElement>()).Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task ExecuteAsync_FixedClock_ReturnsLocalUtcAndUnixTime()
        {
            var fixedTime = new DateTimeOffset(2026, 11, 18, 14, 3, 5, TimeSpan.FromHours(8));
            var tool = new GetCurrentTimeTool(() => fixedTime);

            string result = await tool.ExecuteAsync(
                new Dictionary<string, JsonElement>(),
                workspaceRoot: null);

            result.Should().Contain("2026-11-18 14:03:05 +08:00");
            result.Should().Contain("2026-11-18T06:03:05Z");
            result.Should().Contain("Wednesday");
            result.Should().Contain(fixedTime.ToUnixTimeSeconds().ToString());
        }

        [Fact]
        public void ResultSummary_IsLocalizedAndIndependentOfResultText()
        {
            var tool = new GetCurrentTimeTool();

            tool.GetResultSummary("any result").Should().NotBeNullOrWhiteSpace();
        }
    }
}
