using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// RequestHandoffTool 的 editSteps 参数测试：
/// 覆盖解析重排、超限截断、空标题过滤、缺省保持 null 及 schema 结构。
/// </summary>
public class RequestHandoffToolTests
{
    #region Helpers

    /// <summary>
    /// 构造一次完整的工具调用参数；传入 editSteps 时附加该键（否则模拟未传）。
    /// </summary>
    private static Dictionary<string, JsonElement> BuildArgs(JsonElement? editSteps = null)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["targetAgent"] = JsonSerializer.SerializeToElement("Edit"),
            ["reason"] = JsonSerializer.SerializeToElement("测试移交"),
            ["taskDescription"] = JsonSerializer.SerializeToElement("执行测试任务"),
        };

        if (editSteps.HasValue)
            args["editSteps"] = editSteps.Value;

        return args;
    }

    /// <summary>
    /// 执行工具并返回捕获到的 HandoffRequest（参数合法时工具必然调用移交处理器）。
    /// </summary>
    private static async Task<HandoffRequest> RunExecuteAsync(Dictionary<string, JsonElement> args)
    {
        HandoffRequest? captured = null;
        var tool = new RequestHandoffTool(request =>
        {
            captured = request;
            return Task.CompletedTask;
        });

        await tool.ExecuteAsync(args, null);
        return captured!;
    }

    #endregion

    #region Execute — editSteps 解析

    /// <summary>2 项 editSteps 正常解析：Index 从 1 顺序重排，标题与描述保留。</summary>
    [Fact]
    public async Task Execute_WithEditSteps_ParsesAndReindexes()
    {
        var args = BuildArgs(JsonSerializer.SerializeToElement(new[]
        {
            new { title = "步骤一", description = "修改 A 文件" },
            new { title = "步骤二", description = "补充测试" },
        }));

        var request = await RunExecuteAsync(args);

        var steps = request.EditSteps;
        steps.Should().NotBeNull();
        var parsed = steps!;
        parsed.Should().HaveCount(2);
        parsed[0].Index.Should().Be(1);
        parsed[0].Title.Should().Be("步骤一");
        parsed[0].Description.Should().Be("修改 A 文件");
        parsed[0].Status.Should().Be(AgentStepStatus.Pending);
        parsed[0].RequiresApproval.Should().BeFalse();
        parsed[1].Index.Should().Be(2);
        parsed[1].Title.Should().Be("步骤二");
        parsed[1].Description.Should().Be("补充测试");
    }

    /// <summary>5 项 editSteps 被防御性截断为前 4 项，且 Index 重新连续。</summary>
    [Fact]
    public async Task Execute_WithMoreThanFourEditSteps_TruncatesToFour()
    {
        var args = BuildArgs(JsonSerializer.SerializeToElement(new[]
        {
            new { title = "S1" },
            new { title = "S2" },
            new { title = "S3" },
            new { title = "S4" },
            new { title = "S5" },
        }));

        var request = await RunExecuteAsync(args);

        var steps = request.EditSteps;
        steps.Should().NotBeNull();
        var parsed = steps!;
        parsed.Should().HaveCount(4);
        parsed[0].Title.Should().Be("S1");
        parsed[3].Title.Should().Be("S4");
        parsed.Select(s => s.Index).Should().Equal(1, 2, 3, 4);
    }

    /// <summary>标题为纯空白的项被过滤，剩余项 Index 仍从 1 连续。</summary>
    [Fact]
    public async Task Execute_WithBlankTitleStep_FiltersItOut()
    {
        var args = BuildArgs(JsonSerializer.SerializeToElement(new[]
        {
            new { title = "   " },
            new { title = "有效步骤" },
        }));

        var request = await RunExecuteAsync(args);

        var steps = request.EditSteps;
        steps.Should().NotBeNull();
        var parsed = steps!;
        parsed.Should().HaveCount(1);
        parsed[0].Index.Should().Be(1);
        parsed[0].Title.Should().Be("有效步骤");
    }

    /// <summary>未传 editSteps 时 EditSteps 保持 null（既有调用方行为不变）。</summary>
    [Fact]
    public async Task Execute_WithoutEditSteps_LeavesNull()
    {
        var args = BuildArgs();

        var request = await RunExecuteAsync(args);

        request.EditSteps.Should().BeNull();
    }

    #endregion

    #region Schema

    /// <summary>工具定义 schema 中包含 editSteps 数组参数（maxItems=4、items.required=[title]）。</summary>
    [Fact]
    public void GetDefinition_IncludesEditStepsSchema()
    {
        var tool = new RequestHandoffTool(_ => Task.CompletedTask);

        var schema = JsonSerializer.SerializeToElement(tool.GetDefinition().Function.Parameters);

        schema.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.TryGetProperty("editSteps", out var editSteps).Should().BeTrue();
        editSteps.GetProperty("type").GetString().Should().Be("array");
        editSteps.GetProperty("maxItems").GetInt32().Should().Be(4);
        editSteps.GetProperty("items").GetProperty("required")[0].GetString().Should().Be("title");
    }

    #endregion
}
