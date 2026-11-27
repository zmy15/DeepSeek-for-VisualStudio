using System;
using System.Collections.Generic;
using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// 工具循环中「验证轮次」的判定与终止决策（从 <see cref="BaseAgent"/> 下沉）。
    /// 全部为无状态静态方法；<see cref="BaseAgent.VerificationRoundStatus"/> 枚举仍保留在
    /// <see cref="BaseAgent"/> 上，此处以限定名引用，避免改动既有测试的 12 处枚举引用。
    /// </summary>
    internal static class VerificationLoopDecider
    {
        /// <summary>
        /// 汇总一轮工具调用中的测试/构建结果。
        /// 只有实际执行了测试且 failed=0 才算 TestsPassed。
        /// </summary>
        /// <param name="toolCalls">本轮工具调用列表。</param>
        /// <param name="toolResults">与 <paramref name="toolCalls"/> 一一对应的结果。</param>
        /// <returns>本轮观察到的验证结果标志组合。</returns>
        internal static BaseAgent.VerificationRoundStatus ClassifyVerificationRound(
            IReadOnlyList<ToolCall> toolCalls,
            IReadOnlyList<string?> toolResults)
        {
            var status = BaseAgent.VerificationRoundStatus.None;
            bool sawBuildCall = false;
            bool allBuildsPassed = true;

            int count = Math.Min(toolCalls?.Count ?? 0, toolResults?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                string toolName = ToolClassifier.NormalizeToolName(toolCalls[i].Function?.Name);
                string? result = toolResults[i];

                if (string.Equals(toolName, "run_tests", StringComparison.OrdinalIgnoreCase))
                {
                    if (RunTestsTool.IsTestRunSuccess(result))
                        status |= BaseAgent.VerificationRoundStatus.TestsPassed;
                    else if (RunTestsTool.IsTestRunFailure(result))
                        status |= BaseAgent.VerificationRoundStatus.TestsFailed;
                }
                else if (string.Equals(toolName, "build_solution", StringComparison.OrdinalIgnoreCase))
                {
                    sawBuildCall = true;
                    if (!BuildSolutionTool.IsSuccessResult(result))
                        allBuildsPassed = false;
                }
            }

            if (sawBuildCall)
            {
                status |= allBuildsPassed
                    ? BaseAgent.VerificationRoundStatus.BuildPassed
                    : BaseAgent.VerificationRoundStatus.BuildFailed;
            }

            return status;
        }

        /// <summary>
        /// 根据上一轮未解决的测试失败与本轮验证结果，决定是否立即终止工具循环。
        /// 测试通过优先于构建；明确失败的测试不能被后续编译成功覆盖。
        /// </summary>
        /// <param name="currentRound">本轮验证结果标志。</param>
        /// <param name="hasUnresolvedTestFailure">此前是否已存在未解决的测试失败。</param>
        /// <returns>是否终止、更新后的未解决失败标志、以及本轮首选的结果状态。</returns>
        internal static (bool ShouldTerminate, bool HasUnresolvedTestFailure, BaseAgent.VerificationRoundStatus PreferredStatus)
            DecideVerificationLoopTermination(
                BaseAgent.VerificationRoundStatus currentRound,
                bool hasUnresolvedTestFailure)
        {
            bool roundHasTestFailure =
                (currentRound & BaseAgent.VerificationRoundStatus.TestsFailed) != 0;
            bool roundHasTestSuccess =
                (currentRound & BaseAgent.VerificationRoundStatus.TestsPassed) != 0;

            if (roundHasTestFailure)
                hasUnresolvedTestFailure = true;
            else if (roundHasTestSuccess)
                hasUnresolvedTestFailure = false;

            bool buildFailed =
                (currentRound & BaseAgent.VerificationRoundStatus.BuildFailed) != 0;

            if (roundHasTestSuccess && !roundHasTestFailure && !buildFailed)
            {
                return (true, hasUnresolvedTestFailure, BaseAgent.VerificationRoundStatus.TestsPassed);
            }

            bool buildPassed =
                (currentRound & BaseAgent.VerificationRoundStatus.BuildPassed) != 0
                && !buildFailed
                && !hasUnresolvedTestFailure
                && !roundHasTestFailure;

            return buildPassed
                ? (true, hasUnresolvedTestFailure, BaseAgent.VerificationRoundStatus.BuildPassed)
                : (false, hasUnresolvedTestFailure, BaseAgent.VerificationRoundStatus.None);
        }

        /// <summary>
        /// 找到与验证结论对应的工具结果索引，用于把完整结果作为本轮最终正文。
        /// </summary>
        /// <param name="toolCalls">本轮工具调用列表。</param>
        /// <param name="preferredStatus">终止决策确定的首选状态。</param>
        /// <returns>匹配的工具结果下标；未找到返回 -1。</returns>
        internal static int FindVerificationResultIndex(
            IReadOnlyList<ToolCall> toolCalls,
            BaseAgent.VerificationRoundStatus preferredStatus)
        {
            string expectedTool = preferredStatus == BaseAgent.VerificationRoundStatus.TestsPassed
                ? "run_tests"
                : "build_solution";

            for (int i = 0; i < toolCalls.Count; i++)
            {
                if (string.Equals(
                    ToolClassifier.NormalizeToolName(toolCalls[i].Function?.Name),
                    expectedTool,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
