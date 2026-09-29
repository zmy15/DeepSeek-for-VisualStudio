using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// GitTool 单元测试 — 验证 git 工具的 Name、Definition、DisplayText、ResultSummary
/// 以及 ExecuteAsync 的参数校验、操作分类、阻塞规则。
/// </summary>
public class GitToolTests
{
    private static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
               ?? new Dictionary<string, JsonElement>();
    }

    #region Tool Identity

    [Fact]
    public void GitTool_HasCorrectName()
    {
        new GitTool().Name.Should().Be("git");
    }

    [Fact]
    public void GitTool_Definition_TypeIsFunction()
    {
        new GitTool().GetDefinition().Type.Should().Be("function");
    }

    [Fact]
    public void GitTool_Definition_HasNonEmptyName()
    {
        new GitTool().GetDefinition().Function.Name.Should().Be("git");
    }

    [Fact]
    public void GitTool_Definition_HasDescription()
    {
        new GitTool().GetDefinition().Function.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GitTool_Definition_HasOperationParameter()
    {
        // Verify that the 'operation' parameter is in the required list
        // The Parameters is a dynamic object; we serialize it to check
        var def = new GitTool().GetDefinition();
        var json = JsonSerializer.Serialize(def.Function.Parameters);
        json.Should().Contain("operation");
        json.Should().Contain("required");
    }

    [Fact]
    public void GitTool_IsGitAvailable_DoesNotThrow()
    {
        // Git detection should not throw even if git is not installed
        var act = () => GitTool.IsGitAvailable;
        act.Should().NotThrow();
    }

    [Fact]
    public void GitTool_GitVersion_IsEmptyWhenNotAvailable()
    {
        if (!GitTool.IsGitAvailable)
            GitTool.GitVersion.Should().BeEmpty();
    }

    #endregion

    #region DisplayText

    [Fact]
    public void GetDisplayText_Status_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"status\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
        text.Should().ContainAny("status", "Status", "状态");
    }

    [Fact]
    public void GetDisplayText_Diff_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"diff\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Log_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"log\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Commit_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"commit\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Push_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"push\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Stash_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"stash\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_Reset_ReturnsReadableText()
    {
        var args = ParseArgs("{\"operation\": \"reset\"}");
        var text = new GitTool().GetDisplayText(args);
        text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDisplayText_AllOperations_ReturnNonEmpty()
    {
        var operations = new[]
        {
            "status", "diff", "log", "show", "describe", "tag", "rev-parse", "reflog", "ls-files",
            "add", "commit", "branch", "checkout", "merge", "pull", "push", "stash", "reset",
        };
        var tool = new GitTool();
        foreach (var op in operations)
        {
            var args = ParseArgs($"{{\"operation\": \"{op}\"}}");
            var text = tool.GetDisplayText(args);
            text.Should().NotBeNullOrEmpty($"display text for '{op}' should not be empty");
        }
    }

    #endregion

    #region ResultSummary

    [Fact]
    public void GetResultSummary_Empty_ReturnsNoResult()
    {
        new GitTool().GetResultSummary("").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetResultSummary_Success_ReturnsSuccess()
    {
        var summary = new GitTool().GetResultSummary("exit code: 0");
        summary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetResultSummary_Error_PreservesError()
    {
        var summary = new GitTool().GetResultSummary("Error: something failed");
        summary.Should().Contain("Error: ");
    }

    [Fact]
    public void GetResultSummary_Blocked_PreservesBlocked()
    {
        var summary = new GitTool().GetResultSummary("[BLOCKED] blocked operation");
        summary.Should().Contain("[BLOCKED] ");
    }

    #endregion

    #region ExecuteAsync — Parameter Validation (no git needed)

    [Fact]
    public async Task ExecuteAsync_MissingOperation_ReturnsError()
    {
        var args = ParseArgs("{}");
        var result = await new GitTool().ExecuteAsync(args, null);
        result.Should().Contain("Error: ");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_ReturnsError()
    {
        var args = ParseArgs("{\"operation\": \"invalid_op\"}");
        var result = await new GitTool().ExecuteAsync(args, null);
        result.Should().Contain("Error: ");
        result.Should().Contain("invalid_op");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyOperation_ReturnsError()
    {
        var args = ParseArgs("{\"operation\": \"\"}");
        var result = await new GitTool().ExecuteAsync(args, null);
        result.Should().Contain("Error: ");
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentWorkspace_ReturnsRepoError()
    {
        // Skip if git is not installed
        if (!GitTool.IsGitAvailable)
            return;

        var args = ParseArgs("{\"operation\": \"status\"}");
        var result = await new GitTool().ExecuteAsync(args, "Z:\\NonExistent\\Path");
        // Should fail with either no-repo or directory-not-found error
        result.Should().ContainAny("Error: ", "git");
    }

    #endregion

    #region Log Search and No-Patch

    [Fact]
    public void BuildGitCommand_Log_WithPickaxeSearch_BuildsDashSCommand()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"search\":\"兼容旧结构\",\"oneline\":true,\"count\":5,\"path\":\"Services/Agents/BaseAgent.cs\"}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().Be("log --oneline -5 -S \"兼容旧结构\" -- \"Services/Agents/BaseAgent.cs\"");
    }

    [Fact]
    public void BuildGitCommand_Log_WithPickaxeRegex_AddsRegexFlag()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"search\":\"Fix.*Agent\",\"searchRegex\":true,\"count\":3}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().Be("log -3 -S \"Fix.*Agent\" --pickaxe-regex");
    }

    [Fact]
    public void BuildGitCommand_Log_WithNoDiff_BuildsLowercaseSCommand()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"noDiff\":true,\"oneline\":true,\"count\":2}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().Be("log --oneline -s -2");
    }

    [Fact]
    public void BuildGitCommand_Log_WithGenericFlags_AppendsWhitelistedFlags()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"count\":5,\"flags\":[\"-s\",\"--all\",\"-S\",\"兼容旧结构\",\"--grep=Agent\"]}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().Be("log -s --all -S \"兼容旧结构\" --grep=\"Agent\" -5");
    }

    [Fact]
    public void BuildGitCommand_Log_WithAttachedShortFlag_QuotesValue()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"count\":3,\"flags\":[\"-SFoo\"]}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().Be("log -S \"Foo\" -3");
    }

    [Fact]
    public void BuildGitCommand_Log_WithDangerousFlag_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"flags\":[\"--output=out.txt\"]}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().Be("log --output=\"out.txt\" -10");
        GitTool.FlagsRequireApproval("log", new[] { "--output=out.txt" }, out var reason)
            .Should().BeTrue();
        reason.Should().Contain("--output=out.txt");
    }

    [Fact]
    public void FlagsRequireApproval_MalformedValueFlag_ReturnsFalse()
    {
        GitTool.FlagsRequireApproval("log", new[] { "-S" }, out _).Should().BeFalse();
    }

    [Fact]
    public void BuildGitCommand_Log_WithValueFlagMissingValue_ReturnsFormatError()
    {
        var args = ParseArgs("{\"operation\":\"log\",\"flags\":[\"-S\"]}");

        var command = new GitTool().BuildGitCommand("log", args, "C:\\repo");

        command.Should().StartWith("Error:");
        command.Should().Contain("-S");
    }

    [Fact]
    public void BuildGitCommand_Commit_WithWhitelistedFlag_AppendsFlag()
    {
        var args = ParseArgs("{\"operation\":\"commit\",\"message\":\"test\",\"flags\":[\"--amend\",\"--no-edit\"]}");

        var command = new GitTool().BuildGitCommand("commit", args, "C:\\repo");

        command.Should().Be("commit --amend --no-edit -m \"test\"");
    }

    [Fact]
    public void BuildGitCommand_WriteOperation_WithDangerousFlag_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\":\"reset\",\"flags\":[\"--hard\"]}");

        var command = new GitTool().BuildGitCommand("reset", args, "C:\\repo");

        command.Should().Be("reset --hard HEAD~1");
        GitTool.IsReadOnlyOperation("reset", "", "", "", false).Should().BeFalse();
        GitTool.FlagsRequireApproval("reset", new[] { "--hard" }, out _).Should().BeTrue();
    }

    [Fact]
    public void BuildGitCommand_Show_WithNoDiff_BuildsLowercaseSCommand()
    {
        var args = ParseArgs("{\"operation\":\"show\",\"reference\":\"HEAD~1\",\"noDiff\":true}");

        var command = new GitTool().BuildGitCommand("show", args, "C:\\repo");

        command.Should().Be("show -s HEAD~1");
    }

    #endregion

    #region Git Approval Policy

    [Fact]
    public void RequiresApproval_ReadOnlyAgentWriteOperation_ReturnsFalse()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Ask,
            "commit",
            isReadOnly: false,
            mode: string.Empty,
            delete: false,
            force: false,
            flags: null,
            out string reason);

        requiresApproval.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void RequiresApproval_ReadOnlyAgentWriteWithDangerousFlag_StillReturnsFalse()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Explore,
            "reset",
            isReadOnly: false,
            mode: "hard",
            delete: false,
            force: false,
            flags: new[] { "--hard" },
            out string reason);

        requiresApproval.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void RequiresApproval_ReadOnlyAgentDangerousReadOperation_ReturnsTrue()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Ask,
            "log",
            isReadOnly: true,
            mode: string.Empty,
            delete: false,
            force: false,
            flags: new[] { "--output=out.txt" },
            out string reason);

        requiresApproval.Should().BeTrue();
        reason.Should().Contain("--output=out.txt");
    }

    [Fact]
    public void RequiresApproval_ReadOnlyAgentNormalReadOperation_ReturnsFalse()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Ask,
            "status",
            isReadOnly: true,
            mode: string.Empty,
            delete: false,
            force: false,
            flags: null,
            out string reason);

        requiresApproval.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void RequiresApproval_WriteAgentNormalWriteOperation_ReturnsFalse()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Edit,
            "commit",
            isReadOnly: false,
            mode: string.Empty,
            delete: false,
            force: false,
            flags: new[] { "--allow-empty" },
            out string reason);

        requiresApproval.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void RequiresApproval_WriteAgentDangerousWriteOperation_ReturnsTrue()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Edit,
            "reset",
            isReadOnly: false,
            mode: "hard",
            delete: false,
            force: false,
            flags: null,
            out string reason);

        requiresApproval.Should().BeTrue();
        reason.Should().Be("--hard");
    }

    [Fact]
    public void RequiresApproval_WriteAgentForcePush_ReturnsTrue()
    {
        bool requiresApproval = GitTool.RequiresApproval(
            AgentType.Edit,
            "push",
            isReadOnly: false,
            mode: string.Empty,
            delete: false,
            force: true,
            flags: null,
            out string reason);

        requiresApproval.Should().BeTrue();
        reason.Should().Be("push");
    }

    #endregion

    #region Merge

    [Fact]
    public void GitTool_Definition_OperationEnum_ContainsMerge()
    {
        var json = JsonSerializer.Serialize(new GitTool().GetDefinition().Function.Parameters);
        json.Should().Contain("\"merge\"");
    }

    [Fact]
    public void GitTool_IsReadOnlyOperation_Merge_IsFalse()
    {
        GitTool.IsReadOnlyOperation("merge", "dev", "", "", false).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"operation\": \"merge\", \"branch\": \"feature/x\"}", "merge \"feature/x\"")]
    [InlineData("{\"operation\": \"merge\", \"branch\": \"feature/x\", \"mode\": \"ff-only\"}", "merge --ff-only \"feature/x\"")]
    [InlineData("{\"operation\": \"merge\", \"branch\": \"feature/x\", \"mode\": \"no-ff\"}", "merge --no-ff \"feature/x\"")]
    [InlineData("{\"operation\": \"merge\", \"branch\": \"feature/x\", \"mode\": \"squash\"}", "merge --squash \"feature/x\"")]
    public void BuildGitCommand_Merge_BuildsSafeCommand(string argsJson, string expected)
    {
        var args = ParseArgs(argsJson);
        var command = new GitTool().BuildGitCommand("merge", args, "C:\\repo");
        command.Should().Be(expected);
    }

    [Fact]
    public async Task ExecuteAsync_MergeWithoutBranch_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"merge\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("[BLOCKED] ");
    }

    #endregion

    #region ExecuteAsync — Blocked Operations

    [Fact]
    public void BuildGitCommand_ResetHard_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\": \"reset\", \"mode\": \"hard\"}");
        var command = new GitTool().BuildGitCommand("reset", args, "C:\\repo");

        command.Should().Be("reset --hard HEAD~1");
        GitTool.IsReadOnlyOperation("reset", "", "hard", "", false).Should().BeFalse();
    }

    [Fact]
    public void BuildGitCommand_PushForceMain_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\": \"push\", \"branch\": \"main\", \"force\": true}");
        var command = new GitTool().BuildGitCommand("push", args, "C:\\repo");

        command.Should().Contain("--force");
        GitTool.IsReadOnlyOperation("push", "main", "", "", false).Should().BeFalse();
    }

    [Fact]
    public void BuildGitCommand_PushForceMaster_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\": \"push\", \"branch\": \"master\", \"force\": true}");
        var command = new GitTool().BuildGitCommand("push", args, "C:\\repo");

        command.Should().Contain("--force");
        GitTool.IsReadOnlyOperation("push", "master", "", "", false).Should().BeFalse();
    }

    [Fact]
    public void BuildGitCommand_PushForceFeature_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\": \"push\", \"branch\": \"feature/test\", \"force\": true}");
        var command = new GitTool().BuildGitCommand("push", args, "C:\\repo");

        command.Should().Contain("--force");
    }

    [Fact]
    public void BuildGitCommand_BranchForceDelete_BuildsCommandForApproval()
    {
        var args = ParseArgs("{\"operation\": \"branch\", \"branch\": \"test\", \"delete\": true, \"force\": true}");
        var command = new GitTool().BuildGitCommand("branch", args, "C:\\repo");

        command.Should().Be("branch -D \"test\"");
        GitTool.IsReadOnlyOperation("branch", "test", "", "", true).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_CommitWithoutMessage_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"commit\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("[BLOCKED] ");
    }

    [Fact]
    public async Task ExecuteAsync_CheckoutWithoutBranch_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        var args = ParseArgs("{\"operation\": \"checkout\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().Contain("[BLOCKED] ");
    }

    #endregion

    #region ExecuteAsync — Agent Permission Check

    [Fact]
    public async Task ExecuteAsync_ExploreAgent_WriteOperation_Blocked()
    {
        // Skip if git or repo unavailable — agent check runs after those checks
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Explore;
        try
        {
            var args = ParseArgs("{\"operation\": \"add\", \"files\": [\"test.cs\"]}");
            var result = await new GitTool().ExecuteAsync(args, root);
            // Explore agent should be blocked from write operations
            result.Should().Contain("[BLOCKED] ");
            result.Should().MatchRegex("(?i)explore|不允许|not permitted|Agent");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_AskAgent_WriteOperation_Blocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Ask;
        try
        {
            var args = ParseArgs("{\"operation\": \"add\", \"files\": [\"test.cs\"]}");
            var result = await new GitTool().ExecuteAsync(args, root);

            result.Should().Contain("[BLOCKED] ");
            result.Should().Contain("Ask");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_EditAgent_WriteOperation_NotBlockedByAgent()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Edit;
        try
        {
            var args = ParseArgs("{\"operation\": \"commit\", \"message\": \"test\"}");
            var result = await new GitTool().ExecuteAsync(args, root);
            // Should NOT contain the agent-blocked message (agent permission error in Chinese)
            result.Should().NotContain("不允许");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_NullAgentType_WriteOperation_NotBlocked()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = null;
        // Use branch --list (safe read operation classified under "branch")
        var args = ParseArgs("{\"operation\": \"branch\"}");
        var result = await new GitTool().ExecuteAsync(args, root);
        result.Should().NotContain("[BLOCKED] ");
        result.Should().NotContain("Agent");
        result.Should().Contain("退出码: 0");
    }

    #endregion

    #region ExecuteAsync — Read-Only Operations (Requires Git)

    [Fact]
    public async Task ExecuteAsync_Status_WithGitRepo_SucceedsOrGivesMeaningfulError()
    {
        if (!GitTool.IsGitAvailable)
            return; // Skip if git not available

        // Use the actual project directory which is a git repo
        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"status\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        // Should either return status output or an error about permissions/state
        result.Should().NotBeNullOrEmpty();
        // Should not be a "not installed" or "unknown operation" error
        result.Should().NotContain("git 未安装");
        result.Should().NotContain("not installed");
        result.Should().NotContain("未知操作");
    }

    [Fact]
    public async Task ExecuteAsync_Log_WithGitRepo_ReturnsCommitHistory()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"log\", \"count\": 3, \"oneline\": true}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    [Fact]
    public async Task ExecuteAsync_Diff_WithGitRepo_ReturnsOutput()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"diff\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    #endregion

    #region ExecuteAsync — Write Operations (Requires Git + Careful)

    [Fact]
    public async Task ExecuteAsync_BranchList_WithGitRepo_Succeeds()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"branch\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    [Fact]
    public async Task ExecuteAsync_StashList_WithGitRepo_Succeeds()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        var args = ParseArgs("{\"operation\": \"stash\", \"mode\": \"list\"}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("退出码: 0");
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("rev-parse")]
    [InlineData("reflog")]
    [InlineData("ls-files")]
    public async Task ExecuteAsync_NewReadOnlyOperations_AreAllowedForAskAgent(string operation)
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Ask;
        try
        {
            var args = ParseArgs($"{{\"operation\": \"{operation}\", \"count\": 3}}");
            var result = await new GitTool().ExecuteAsync(args, root);

            result.Should().NotContain("[BLOCKED] ");
            result.Should().NotContain("不允许");
            result.Should().Contain("退出码: 0");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Describe_IsAllowedForAskAgent()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Ask;
        try
        {
            var args = ParseArgs("{\"operation\": \"describe\"}");
            var result = await new GitTool().ExecuteAsync(args, root);

            result.Should().NotContain("[BLOCKED] ");
            result.Should().NotContain("不允许");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_StashShow_IsAllowedForAskAgent()
    {
        var root = GetProjectRoot();
        if (root == null || !GitTool.IsGitAvailable) return;

        GitTool.CurrentAgentType = AgentType.Ask;
        try
        {
            var args = ParseArgs("{\"operation\": \"stash\", \"mode\": \"show\"}");
            var result = await new GitTool().ExecuteAsync(args, root);

            result.Should().NotContain("[BLOCKED] ");
            result.Should().NotContain("不允许");
        }
        finally
        {
            GitTool.CurrentAgentType = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_AddWithSpecificFile_ReturnsExpectedOutput()
    {
        if (!GitTool.IsGitAvailable)
            return;

        var workspaceRoot = GetProjectRoot();
        if (workspaceRoot == null)
            return;

        // Stage a file that definitely exists
        var args = ParseArgs("{\"operation\": \"add\", \"files\": [\"README.md\"]}");
        var result = await new GitTool().ExecuteAsync(args, workspaceRoot);

        result.Should().NotBeNullOrEmpty();
        // If README.md is already tracked, it may have no output; that's OK
        // git add on an already-staged file returns exit code 0 with no stdout
    }

    #endregion

    #region Git Detection

    [Fact]
    public void IsGitAvailable_ReturnsBoolean()
    {
        // Should not throw when accessed
        var available = GitTool.IsGitAvailable;
        // available is a bool — just verify it doesn't throw
    }

    [Fact]
    public void GitVersion_IsNonEmpty_WhenGitAvailable()
    {
        if (GitTool.IsGitAvailable)
        {
            GitTool.GitVersion.Should().NotBeNullOrEmpty();
            GitTool.GitVersion.Should().Contain("git version");
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the project root directory (the git repo root).
    /// </summary>
    private static string? GetProjectRoot()
    {
        // Walk up from the test assembly location to find the repo root
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, ".git")))
                    return dir;
                var parent = System.IO.Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    #endregion
}
