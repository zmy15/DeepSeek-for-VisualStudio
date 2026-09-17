using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>
    /// multi_replace_string_in_file 工具 — 批量字符串替换。
    /// 委托给 ReplaceStringInFileTool 逐条执行。
    /// </summary>
    public class MultiReplaceStringInFileTool : BuiltInToolBase
    {
        private readonly ReplaceStringInFileTool _singleReplacer = new();

        /// <summary>
        /// StagedEditWorkspace 引用（可选注入），传递给内部 ReplaceStringInFileTool。
        /// </summary>
        public Services.Editing.StagedEditWorkspace? Workspace
        {
            set => _singleReplacer.Workspace = value;
        }

        public override string Name => "multi_replace_string_in_file";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = "multi_replace_string_in_file",
                    Description = L["tool.multi_replace_string_in_file.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            replacements = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        filePath = new { type = "string", description = LocalizationService.Instance["tool.multiReplace.param.filePath"] },
                                        oldString = new { type = "string", description = LocalizationService.Instance["tool.multiReplace.param.oldString"] },
                                        newString = new { type = "string", description = LocalizationService.Instance["tool.multiReplace.param.newString"] }
                                    },
                                    required = new[] { "filePath", "oldString", "newString" }
                                },
                                description = LocalizationService.Instance["tool.multiReplace.param.replacements"]
                            },
                            expected = new
                            {
                                type = "string",
                                description = LocalizationService.Instance["tool.editVerify.expectedDescription"]
                            }
                        },
                        required = new[] { "replacements", "expected" }
                    }
                }
            };
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            int count = 0;
            if (args.TryGetValue("replacements", out var repsElement)
                && repsElement.ValueKind == JsonValueKind.Array)
                count = repsElement.GetArrayLength();
            return count > 0
                ? LocalizationService.Instance.Format("tool.multiReplace.batchEditCount", count)
                : LocalizationService.Instance["tool.multiReplace.batchEdit"];
        }

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult)) return LocalizationService.Instance["tool.common.noResult"];
            if (toolResult.StartsWith("Error: ")) return toolResult;
            if (toolResult.Contains("成功") || toolResult.Contains("success"))
                return LocalizationService.Instance["tool.multiReplace.editComplete"];
            return LocalizationService.Instance["tool.multiReplace.editDone"];
        }

        public override async Task<string> ExecuteAsync(Dictionary<string, JsonElement> args, string? workspaceRoot)
        {
            string expectedText = GetStringArg(args, "expected");
            if (string.IsNullOrEmpty(expectedText))
                return LocalizationService.Instance["tool.editVerify.missingExpected"];

            var expectedParse = ExpectedContentVerifier.ParseLineNumberedFragment(expectedText);
            if (!expectedParse.Success)
                return expectedParse.Error;

            if (!args.TryGetValue("replacements", out var element) ||
                element.ValueKind != JsonValueKind.Array)
                return LocalizationService.Instance["tool.multiReplace.missingReplacements"];

            // 预校验全部替换项指向同一文件；校验通过前不做任何写入，
            // 避免多文件调用在应用中途被拒绝后留下“改了一半”的状态。
            var pending = new List<(string FilePath, Dictionary<string, JsonElement> Args)>();
            int invalidCount = 0;
            string? targetFile = null;
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    invalidCount++;
                    continue;
                }

                var singleArgs = new Dictionary<string, JsonElement>();
                foreach (var prop in item.EnumerateObject())
                    singleArgs[prop.Name] = prop.Value;

                string filePath = GetStringArg(singleArgs, "filePath");
                string oldStr = GetStringArg(singleArgs, "oldString");

                if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(oldStr))
                {
                    invalidCount++;
                    continue;
                }

                string resolvedPath = ResolvePath(filePath, workspaceRoot);
                if (targetFile == null)
                {
                    targetFile = resolvedPath;
                }
                else if (!string.Equals(
                    targetFile, resolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return LocalizationService.Instance["tool.editVerify.verificationSingleTarget"];
                }

                pending.Add((resolvedPath, singleArgs));
            }

            var results = new List<string>();
            int successCount = 0;
            int failCount = invalidCount;

            // 捕获批量前快照，任一条替换失败时整体回滚，让 AI 面对的是确定状态而非部分生效。
            string? before = targetFile == null
                ? null
                : await _singleReplacer.ReadCurrentContentAsync(targetFile);

            foreach (var (filePath, singleArgs) in pending)
            {
                string result = await _singleReplacer.ApplyReplacementAsync(
                    singleArgs, workspaceRoot, expectedText: null, verifyAfterWrite: false);
                results.Add($"{Path.GetFileName(filePath)}: {result}");
                if (result.StartsWith("Error: ") || result.StartsWith("Timeout: ")) failCount++;
                else successCount++;
            }

            string summary = $"multi_replace_string_in_file: success {successCount}, fail {failCount}";
            string detail = summary + "\n" + string.Join("\n", results);

            if (failCount > 0 || targetFile == null)
            {
                if (targetFile != null && before != null)
                {
                    try
                    {
                        await _singleReplacer.RestoreFileContentAsync(targetFile, before);
                    }
                    catch (Exception ex)
                    {
                        detail += "\n" + $"Rollback failed: {ex.Message}";
                    }
                }

                if (targetFile != null)
                    detail += "\n" + await _singleReplacer.BuildCurrentStateSnapshotAsync(targetFile);
                return detail;
            }

            string? actualContent = await _singleReplacer.ReadCurrentContentAsync(targetFile!);
            string verification = ExpectedContentVerifier.VerifyExpectedFragment(
                expectedText, actualContent, targetFile!);
            return detail + "\n" + verification;
        }
    }
}
