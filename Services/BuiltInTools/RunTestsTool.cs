using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// run_tests 工具 — 运行工作区中的 .NET 或 C++ 单元测试并返回结构化结果。
    /// 委托给 <see cref="ITestService"/> 执行实际测试。
    /// </summary>
    public class RunTestsTool : BuiltInToolBase
    {
        private readonly ITestService? _testService;

        public RunTestsTool(ITestService? testService = null)
        {
            _testService = testService;
        }

        public override string Name => "run_tests";

        /// <summary>
        /// "工作区没有测试项目"的稳定标记。带前缀是为了让上层（EditAgent 的验证状态跟踪）
        /// 在不依赖本地化文案的前提下识别该分支，进而回退到构建验证。
        /// </summary>
        internal const string NoTestProjectMarker = "NO_TESTS:";

        /// <summary>存在多个测试项目、需要显式指定时的稳定标记。</summary>
        internal const string MultipleProjectsMarker = "MULTIPLE_TEST_PROJECTS:";

        /// <summary>
        /// 判断 run_tests 结果是否属于"明确失败"。
        ///
        /// 采用白名单式失败判定（而非"成功判定"）：只有出现 failed&gt;0、
        /// Error: 前缀或 Timeout: 前缀才算失败，其余一律视为通过。
        /// 理由：把成功误判为失败会让系统多跑一次昂贵的构建，代价明显高于
        /// 漏判（漏判时最终答案仍会带上测试输出供人工核对）。
        /// </summary>
        internal static bool IsTestRunFailure(string? toolResult)
        {
            if (string.IsNullOrWhiteSpace(toolResult))
                return false;

            if (toolResult.StartsWith("Error: ", StringComparison.OrdinalIgnoreCase) ||
                toolResult.StartsWith("Timeout: ", StringComparison.OrdinalIgnoreCase))
                return true;

            var match = System.Text.RegularExpressions.Regex.Match(
                toolResult, @"failed=(\d+)");
            return match.Success
                && int.TryParse(match.Groups[1].Value, out int failed)
                && failed > 0;
        }

        /// <summary>
        /// 判断一次测试调用是否形成了有效的通过结论。
        ///
        /// 必须实际执行了至少一个用例，且没有 Error / Timeout 前缀或失败用例。
        /// <c>total=0</c> 只表示测试流程结束，不表示代码行为已被验证。
        /// </summary>
        internal static bool IsTestRunSuccess(string? toolResult)
        {
            if (string.IsNullOrWhiteSpace(toolResult)
                || toolResult.StartsWith(NoTestProjectMarker, StringComparison.Ordinal)
                || toolResult.StartsWith(MultipleProjectsMarker, StringComparison.Ordinal)
                || IsTestRunFailure(toolResult))
            {
                return false;
            }

            var totalMatch = System.Text.RegularExpressions.Regex.Match(
                toolResult, @"total=(\d+)");
            var failedMatch = System.Text.RegularExpressions.Regex.Match(
                toolResult, @"failed=(\d+)");

            return totalMatch.Success
                && int.TryParse(totalMatch.Groups[1].Value, out int total)
                && total > 0
                && failedMatch.Success
                && int.TryParse(failedMatch.Groups[1].Value, out int failed)
                && failed == 0;
        }

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "run_tests",
                    Description = L["tool.run_tests.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            project = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runTests.param.project"]
                            },
                            filter = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runTests.param.filter"]
                            },
                            configuration = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.runTests.param.configuration"]
                            }
                        },
                        required = new string[] { }
                    }
                }
            };
        }

        public override async Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            var testService = _testService;
            if (testService == null)
                return LocalizationService.Instance["tool.runTests.serviceNotInit"];

            string? project = NullIfEmpty(GetStringArg(args, "project"));
            string? filter = NullIfEmpty(GetStringArg(args, "filter"));
            string? configuration = NullIfEmpty(GetStringArg(args, "configuration"));

            TestRunResult result;
            try
            {
                result = await testService.RunTestsAsync(
                    workspaceRoot, project, filter, configuration, CancellationToken);
            }
            catch (Exception ex)
            {
                return LocalizationService.Instance.Format(
                    "tool.runTests.failed", ex.Message);
            }

            return FormatResult(result);
        }

        /// <summary>
        /// 把结构化测试结果渲染成给模型看的文本。
        /// </summary>
        internal static string FormatResult(TestRunResult result)
        {
            var L = LocalizationService.Instance;

            switch (result.Error)
            {
                case TestRunErrorKind.NoTestProject:
                    return NoTestProjectMarker + L["tool.runTests.noTestProject"];

                case TestRunErrorKind.MultipleProjects:
                    var sb = new StringBuilder(MultipleProjectsMarker + L["tool.runTests.multipleProjects"]);
                    sb.AppendLine();
                    foreach (string p in result.CandidateProjects)
                        sb.AppendLine($"- {p}");
                    return sb.ToString().TrimEnd();

                case TestRunErrorKind.RunnerNotFound:
                    return L["tool.runTests.runnerNotFound"];

                case TestRunErrorKind.LaunchFailed:
                    return L["tool.runTests.launchFailed"];

                case TestRunErrorKind.Timeout:
                    return L["tool.runTests.timeout"];

                case TestRunErrorKind.BuildFailed:
                    var buildFailed = new StringBuilder(result.Kind == TestProjectKind.Cpp
                        ? L["tool.runTests.cmakeBuildFailed"]
                        : L["tool.runTests.buildFailed"]);
                    if (!string.IsNullOrWhiteSpace(result.OutputTail))
                    {
                        buildFailed.AppendLine();
                        buildFailed.AppendLine("```");
                        buildFailed.AppendLine(result.OutputTail);
                        buildFailed.Append("```");
                    }
                    return buildFailed.ToString();

                case TestRunErrorKind.NoResultProduced:
                    var noResult = new StringBuilder(L["tool.runTests.noResultProduced"]);
                    if (!string.IsNullOrWhiteSpace(result.OutputTail))
                    {
                        noResult.AppendLine();
                        noResult.AppendLine("```");
                        noResult.AppendLine(result.OutputTail);
                        noResult.Append("```");
                    }
                    return noResult.ToString();
            }

            // ── 正常跑完：输出计数 + 失败明细 ──
            var output = new StringBuilder();

            // 首行是稳定的机器可读摘要（不随语言变化），供 GetResultSummary 与模型快速定位
            output.AppendLine(string.Format(
                "total={0} passed={1} failed={2} skipped={3} elapsed={4}s",
                result.Total, result.Passed, result.Failed, result.Skipped,
                result.Duration.TotalSeconds.ToString("F1")));

            output.AppendLine(string.Format(
                L["tool.runTests.summary"],
                result.Total, result.Passed, result.Failed, result.Skipped,
                result.Duration.TotalSeconds.ToString("F1")));

            // total=0 常见于过滤器未匹配到用例或测试发现失败。
            // 明确提示模型这不是一次有效验证，避免 failed=0 被误当成全部通过。
            if (result.Total == 0)
                output.AppendLine(L["tool.runTests.noTestsExecuted"]);

            if (result.Failed > 0)
            {
                output.AppendLine();
                output.AppendLine(L["tool.runTests.failedDetails"]);
                foreach (var failed in result.FailedTests)
                {
                    output.AppendLine($"- {failed.Name}");
                    if (!string.IsNullOrWhiteSpace(failed.Message))
                        output.AppendLine($"  {failed.Message}");
                }

                if (result.Failed > result.FailedTests.Count)
                {
                    output.AppendLine(string.Format(
                        L["tool.runTests.moreFailures"],
                        result.Failed - result.FailedTests.Count));
                }
            }

            return output.ToString().TrimEnd();
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string filter = GetStringArg(args, "filter");
            return string.IsNullOrEmpty(filter)
                ? LocalizationService.Instance["tool.runTests.displayText"]
                : LocalizationService.Instance.Format("tool.runTests.displayTextWithFilter", filter);
        }

        public override string GetResultSummary(string toolResult)
        {
            string L(string key) => LocalizationService.Instance[key];

            if (string.IsNullOrEmpty(toolResult)) return L("tool.common.noResult");
            if (toolResult.StartsWith("Error: ")) return toolResult;

            // 稳定标记：直接回显，避免被下面的计数正则误判
            if (toolResult.StartsWith(NoTestProjectMarker))
                return toolResult.Substring(NoTestProjectMarker.Length);
            if (toolResult.StartsWith(MultipleProjectsMarker))
                return toolResult.Substring(MultipleProjectsMarker.Length);

            var match = System.Text.RegularExpressions.Regex.Match(
                toolResult, @"passed=(\d+) failed=(\d+) skipped=(\d+)");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out int passed) &&
                int.TryParse(match.Groups[2].Value, out int failed) &&
                int.TryParse(match.Groups[3].Value, out int skipped))
            {
                int total = passed + failed + skipped;
                if (total == 0)
                    return L("tool.runTests.noTestsExecuted");

                return failed == 0
                    ? L("tool.runTests.summaryAllPassed")
                    : string.Format(L("tool.runTests.summarySomeFailed"), failed, total);
            }

            return L("tool.runTests.complete");
        }

        private static string? NullIfEmpty(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
