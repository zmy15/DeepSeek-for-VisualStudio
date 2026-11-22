using System.Collections.Generic;

namespace BenchmarkRunner
{
    internal static class ExecutionEnvironment
    {
        public static Dictionary<string, object?> Describe(HarnessProfile profile)
        {
            return new Dictionary<string, object?>
            {
                ["agent_runtime"] = "production AgentFactory/BaseAgent",
                ["api_runtime"] = "production DeepSeekApiService",
                ["build_service"] = "headless HeadlessBuildService (dotnet/cmake CLI)",
                ["test_service"] = "headless HeadlessTestService (dotnet/ctest CLI)",
                ["visual_studio_environment"] = "not used",
                ["mcp"] = "disabled",
                ["web_search"] = "disabled",
                ["memory"] = "disabled",
                ["skills"] = "production SkillService",
                ["ide_context"] = "headless workspace + Git context",
                ["approval_policy"] = "headless workspace allowlist",
                ["workflow"] = "headless Agent/handoff loop",
                ["harness_profile"] = profile.Id,
                ["thinking_enabled"] = profile.ThinkingEnabled,
                ["thinking_effort"] = profile.ThinkingEffort,
            };
        }
    }
}
