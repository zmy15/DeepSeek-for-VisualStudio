using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkRunner
{
    /// <summary>
    /// Presents benchmark-only tool implementations to BaseAgent through the MCP
    /// dispatch path. Production built-in tool definitions remain the schema source,
    /// while execution is routed here before Visual Studio-specific code is touched.
    /// </summary>
    internal sealed class HeadlessToolProxyClient : IMcpClient
    {
        private readonly HeadlessToolBridge _bridge;

        public HeadlessToolProxyClient(HeadlessToolBridge bridge)
        {
            _bridge = bridge;
            Tools = bridge.ToolNames
                .Select(name => new McpTool
                {
                    Name = name,
                    Description = $"Headless benchmark adapter for {name}.",
                    InputSchema = new McpInputSchema(),
                })
                .ToList();
        }

        public string ServerName => "headless-tool-proxy";
        public string Transport => "in-process";
        public bool IsConnected => true;
        public InitializeResult? ServerInfo { get; } = new()
        {
            ProtocolVersion = "2025-11-25",
            ServerInfo = new ImplementationInfo
            {
                Name = "headless-tool-proxy",
                Version = "1.0",
            },
        };

        public List<McpTool> Tools { get; }

        public Task ConnectAsync(
            CancellationToken cancellationToken = default,
            Action<string>? progress = null)
        {
            progress?.Invoke("in-process");
            return Task.CompletedTask;
        }

        public async Task<ToolCallResult> CallToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            string text = await _bridge.ExecuteAsync(
                toolName,
                arguments,
                cancellationToken);
            return new ToolCallResult
            {
                IsError = text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase),
                Content = new List<ToolContentItem>
                {
                    new()
                    {
                        Type = "text",
                        Text = text,
                    },
                },
            };
        }

        public Task RefreshToolsAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    internal sealed class HeadlessToolProxyHost : IDisposable
    {
        private HeadlessToolProxyHost(
            McpManagerService manager,
            HeadlessToolBridge bridge,
            HeadlessToolProxyClient client)
        {
            Manager = manager;
            Bridge = bridge;
            Client = client;
        }

        public McpManagerService Manager { get; }
        public HeadlessToolBridge Bridge { get; }
        private HeadlessToolProxyClient Client { get; }

        public static HeadlessToolProxyHost Create(
            string workspaceRoot,
            HeadlessBuildService buildService)
        {
            var manager = new McpManagerService();
            var bridge = new HeadlessToolBridge(workspaceRoot, buildService);
            var client = new HeadlessToolProxyClient(bridge);
            AddClient(manager, client);
            return new HeadlessToolProxyHost(manager, bridge, client);
        }

        public void Dispose()
        {
            Client.Dispose();
        }

        private static void AddClient(
            McpManagerService manager,
            IMcpClient client)
        {
            FieldInfo? field = typeof(McpManagerService).GetField(
                "_clients",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(manager) is not List<IMcpClient> clients)
            {
                throw new InvalidOperationException(
                    "Could not register the headless tool proxy.");
            }
            clients.Add(client);
        }
    }
}
