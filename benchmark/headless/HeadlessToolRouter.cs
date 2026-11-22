using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    /// <summary>
    /// Routes benchmark-owned tools first and falls back to the production
    /// BuiltInToolService for services that already work without Visual Studio.
    /// </summary>
    internal sealed class HeadlessToolRouter
    {
        public HeadlessToolRouter(
            BuiltInToolService builtIn,
            HeadlessToolBridge bridge)
        {
            BuiltIn = builtIn;
            Bridge = bridge;
        }

        public BuiltInToolService BuiltIn { get; }
        public HeadlessToolBridge Bridge { get; }

        public IReadOnlyList<string> RegisteredToolNames
        {
            get
            {
                return BuiltIn.GetFullToolDefinitions()
                    .Select(definition => definition.Function.Name)
                    .Concat(Bridge.ToolNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public async Task<string?> ExecuteBuiltInToolAsync(
            string toolName,
            string argumentsJson,
            string? workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            if (Bridge.Handles(toolName))
            {
                Dictionary<string, object> args;
                try
                {
                    args = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        argumentsJson)
                        ?? new Dictionary<string, object>();
                }
                catch (JsonException)
                {
                    args = new Dictionary<string, object>();
                }
                return await Bridge.ExecuteAsync(
                    toolName,
                    args,
                    cancellationToken);
            }

            return await BuiltIn.ExecuteBuiltInToolAsync(
                toolName,
                argumentsJson,
                workspaceRoot,
                cancellationToken);
        }
    }
}
