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
                ["diagnostics"] = "benchmark proxy parses CLI compiler output",
                ["symbols"] = "benchmark source scanner",
                ["file_comments"] = "benchmark source comment scanner",
                ["file_tools"] = "benchmark disk-only proxy",
                ["visual_studio_environment"] = "not used by benchmark tool proxy",
                ["mcp"] = "user MCP disabled; in-process benchmark tool proxy active",
                ["fetch_webpage"] = "disabled when web-search backend is absent",
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
