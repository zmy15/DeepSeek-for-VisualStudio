using DeepSeek_v4_for_VisualStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.BuiltInTools
{
    /// <summary>Lists all skills available in the current workspace.</summary>
    public sealed class ListSkillsTool : BuiltInToolBase
    {
        private readonly ISkillService _skillService;

        public ListSkillsTool(ISkillService skillService)
        {
            _skillService = skillService ?? throw new ArgumentNullException(nameof(skillService));
        }

        public override string Name => "list_skills";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = Name,
                    Description = L["tool.listSkills.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>(),
                    },
                },
            };
        }

        public override async Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args,
            string? workspaceRoot)
        {
            var discovery = await _skillService.DiscoverSkillsAsync(workspaceRoot);
            return FormatSkills(discovery);
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
            => L["tool.listSkills.displayText"];

        public override string GetResultSummary(string toolResult)
        {
            if (string.IsNullOrEmpty(toolResult))
                return L["tool.common.noResult"];
            if (toolResult.StartsWith("Error: "))
                return toolResult;
            return L["tool.listSkills.resultSummary"];
        }

        private static string FormatSkills(SkillDiscoveryResult discovery)
        {
            var skills = discovery.Skills
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(skill => skill.Source)
                .ToList();
            var builder = new StringBuilder();

            builder.AppendLine(L.Format("tool.listSkills.resultHeader", skills.Count));
            if (skills.Count == 0)
            {
                builder.AppendLine();
                builder.Append(L["tool.listSkills.noSkills"]);
                return builder.ToString();
            }

            builder.AppendLine();
            builder.AppendLine(
                $"| {L["tool.listSkills.tableName"]} | {L["tool.listSkills.tableDescription"]} | "
                + $"{L["tool.listSkills.tableSource"]} | {L["tool.listSkills.tableUsage"]} |");
            builder.AppendLine("|---|---|---|---|");

            foreach (var skill in skills)
            {
                string description = EscapeTableCell(
                    string.IsNullOrWhiteSpace(skill.Description) ? "-" : skill.Description);
                builder.AppendLine(
                    $"| `{EscapeTableCell(skill.Name)}` | {description} | "
                    + $"{GetSourceLabel(skill.Source)} | {GetUsageLabel(skill)} |");
            }

            return builder.ToString().TrimEnd();
        }

        private static string GetSourceLabel(SkillSource source)
        {
            return source switch
            {
                SkillSource.Project => L["tool.listSkills.sourceProject"],
                SkillSource.User => L["tool.listSkills.sourceUser"],
                SkillSource.BuiltIn => L["tool.listSkills.sourceBuiltIn"],
                _ => source.ToString(),
            };
        }

        private static string GetUsageLabel(SkillDefinition skill)
        {
            if (skill.AlwaysInject)
                return L["tool.listSkills.usageAlwaysInject"];
            if (skill.DisableModelInvocation)
            {
                return skill.UserInvocable
                    ? L["tool.listSkills.usageUserOnly"]
                    : L["tool.listSkills.usageUnavailable"];
            }
            return skill.UserInvocable
                ? L["tool.listSkills.usageModelAndUser"]
                : L["tool.listSkills.usageModelOnly"];
        }

        private static string EscapeTableCell(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("|", "\\|")
                .Replace("`", "\\`")
                .Replace("\r\n", "<br>")
                .Replace("\r", "<br>")
                .Replace("\n", "<br>");
        }
    }

    /// <summary>Supports loading full skill instructions by name.</summary>
    public sealed class LoadSkillTool : BuiltInToolBase
    {
        private readonly ISkillService _skillService;

        public LoadSkillTool(ISkillService skillService)
        {
            _skillService = skillService ?? throw new ArgumentNullException(nameof(skillService));
        }

        public override string Name => "load_skill";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = Name,
                    Description = L["tool.loadSkill.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = L["tool.loadSkill.param.name"] },
                        },
                        required = new[] { "name" },
                    },
                },
            };
        }

        public override async Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args,
            string? workspaceRoot)
        {
            string name = GetStringArg(args, "name").Trim();
            if (name.Length == 0)
                return L["tool.loadSkill.missingName"];

            var discovery = await _skillService.DiscoverSkillsAsync(workspaceRoot);
            var skill = _skillService.FindSkill(name, discovery);
            if (skill == null)
                return L.Format("tool.loadSkill.notFound", name);
            if (skill.DisableModelInvocation)
                return L.Format("tool.loadSkill.disabled", name);

            return skill.GetFullInstructions();
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string name = GetStringArg(args, "name");
            return string.IsNullOrWhiteSpace(name)
                ? L["tool.loadSkill.displayText"]
                : L.Format("tool.loadSkill.displayTextNamed", name);
        }

        public override string GetResultSummary(string toolResult)
            => L["tool.loadSkill.resultSummary"];
    }

    /// <summary>Supports reading a resource that belongs to a loaded skill.</summary>
    public sealed class ReadSkillResourceTool : BuiltInToolBase
    {
        private readonly ISkillService _skillService;

        public ReadSkillResourceTool(ISkillService skillService)
        {
            _skillService = skillService ?? throw new ArgumentNullException(nameof(skillService));
        }

        public override string Name => "read_skill_resource";

        public override ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Type = "function",
                Function = new ToolFunction
                {
                    Name = Name,
                    Description = L["tool.readSkillResource.desc"],
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            skillName = new { type = "string", description = L["tool.readSkillResource.param.skillName"] },
                            relativePath = new { type = "string", description = L["tool.readSkillResource.param.relativePath"] },
                        },
                        required = new[] { "skillName", "relativePath" },
                    },
                },
            };
        }

        public override async Task<string> ExecuteAsync(
            Dictionary<string, JsonElement> args,
            string? workspaceRoot)
        {
            string skillName = GetStringArg(args, "skillName").Trim();
            string relativePath = GetStringArg(args, "relativePath").Trim();
            if (skillName.Length == 0)
                return L["tool.readSkillResource.missingSkill"];
            if (relativePath.Length == 0)
                return L["tool.readSkillResource.missingPath"];

            var discovery = await _skillService.DiscoverSkillsAsync(workspaceRoot);
            var skill = _skillService.FindSkill(skillName, discovery);
            if (skill == null)
                return L.Format("tool.readSkillResource.notFound", skillName);
            if (skill.DisableModelInvocation)
                return L.Format("tool.readSkillResource.disabled", skillName);

            if (string.IsNullOrEmpty(skill.RootDirectory))
                return L.Format("tool.readSkillResource.resourceNotFound", relativePath);

            string requestedPath;
            try
            {
                requestedPath = Path.GetFullPath(Path.Combine(skill.RootDirectory, relativePath));
            }
            catch
            {
                return L.Format("tool.readSkillResource.resourceNotFound", relativePath);
            }

            bool isListedResource = skill.ResourceFiles.Any(file =>
                string.Equals(
                    Path.GetFullPath(file),
                    requestedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (!isListedResource)
                return L.Format("tool.readSkillResource.resourceNotFound", relativePath);

            string? content = _skillService.ReadSkillResource(skill, relativePath);
            return content ?? L.Format("tool.readSkillResource.resourceNotFound", relativePath);
        }

        public override string GetDisplayText(Dictionary<string, JsonElement> args)
        {
            string skillName = GetStringArg(args, "skillName");
            string relativePath = GetStringArg(args, "relativePath");
            return L.Format("tool.readSkillResource.displayText", skillName, relativePath);
        }

        public override string GetResultSummary(string toolResult)
            => L["tool.readSkillResource.resultSummary"];
    }
}
