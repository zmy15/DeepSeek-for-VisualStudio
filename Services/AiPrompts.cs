using System;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 集中管理所有发送给 AI 的 Prompt 文本。
    /// 支持多语言：根据 LocalizationService 当前语言自动切换中英文。
    /// 所有 Prompt 通过 LocalizationService 获取，便于统一修改、多语言支持和 A/B 测试。
    /// </summary>
    public static class AiPrompts
    {
        /// <summary>
        /// 便捷访问器。
        /// </summary>
        private static LocalizationService L => LocalizationService.Instance;

        #region System Prompts

        /// <summary>
        /// 默认系统提示词，定义 AI 助手的行为角色。
        /// 用户可在 工具 → 选项 → DeepSeek Chat → API Settings → System Prompt 中自定义覆盖。
        /// </summary>
        public static string DefaultSystemPrompt => L["system.defaultSystemPrompt"];

        /// <summary>
        /// 英文版默认系统提示词（始终返回英文，不受当前语言设置影响）。
        /// 用于在选项页中展示英文版 System Prompt。
        /// </summary>
        public static string DefaultSystemPromptEn => L.GetValueForLocale("system.defaultSystemPrompt", "en");

        /// <summary>
        /// 上一版内置中文系统提示词。
        /// 仅用于识别并迁移仍保持旧默认值的设置，不能覆盖用户自定义提示词。
        /// </summary>
        public static string PreviousDefaultSystemPrompt =>
            L.GetValueForLocale("system.defaultSystemPrompt", "zh-CN").Replace(
                "仅当缺少关键参数且不同理解会导致明显不同的执行结果时才提问；意图明确的指令直接执行，不要反复确认或质疑。",
                "如果用户的问题模糊不清，先追问澄清再给出建议。");

        /// <summary>
        /// 上一版内置英文系统提示词。
        /// 仅用于识别并迁移仍保持旧默认值的设置，不能覆盖用户自定义提示词。
        /// </summary>
        public static string PreviousDefaultSystemPromptEn =>
            L.GetValueForLocale("system.defaultSystemPrompt", "en").Replace(
                "Ask only when a critical parameter is missing or different interpretations would produce materially different results; execute clear instructions directly without repeated confirmation or second-guessing.",
                "If the user's question is ambiguous, ask for clarification before making suggestions.");

        #endregion

        #region UI Messages

        /// <summary>
        /// 首次启动时的欢迎语，当已配置 API Key 时显示。
        /// </summary>
        public static string WelcomeMessage => L["ui.welcomeMessage"];

        /// <summary>
        /// 未配置 API Key 时显示的警告消息。
        /// </summary>
        public static string ApiKeyMissingMessage => L["ui.apiKeyMissingMessage"];

        #endregion

        #region Skill Prompts

        /// <summary>
        /// Skill 系统提示词片段 — 注入到主系统提示中，告知 AI 如何使用技能。
        /// 包含详细的调用时机说明和语义匹配指导。
        /// </summary>
        public static string SkillSystemPromptFragment => L["system.skillSystemPromptFragment"];

        /// <summary>
        /// 构建 Skill 系统提示词，使用占位符替换以避免模板中的字面量大括号被
        /// <see cref="string.Format(string, object)"/> 误解析为格式项。
        /// </summary>
        public static string BuildSkillSystemPromptFragment(string discoveryContext)
            => SkillSystemPromptFragment.Replace("{0}", discoveryContext ?? string.Empty)
                + L["system.skillExecutionPolicy"];

        /// <summary>
        /// 技能路由判断 — 系统提示词。
        /// 用于在用户提问时，先让 AI 判断是否应调用某个技能。
        /// </summary>
        public static string SkillRoutingSystemPrompt => L["system.skillRoutingSystemPrompt"];

        /// <summary>
        /// 技能路由判断 — 用户提示词模板。
        /// {0} = 技能总结
        /// {1} = 用户问题 + 上下文
        /// </summary>
        public static string SkillRoutingUserPrompt => L["system.skillRoutingUserPrompt"];

        /// <summary>
        /// 内置示例技能：代码审查 (code-review)。
        /// 演示 SKILL.md 的标准格式。
        /// </summary>
        public static string BuiltInSkill_CodeReview => L["system.builtInSkillCodeReview"];

        #endregion

        #region Multi-Agent Prompts

        /// <summary>
        /// 多 Agent 系统提示词片段 — 告知 AI 当前处于多 Agent 协作环境。
        /// </summary>
        public static string MultiAgentSystemPromptFragment => L["system.multiAgentSystemPromptFragment"];

        /// <summary>
        /// 记忆系统提示词片段 — 告知 AI 何时应主动使用 memory 工具存储偏好、项目知识等。
        /// </summary>
        public static string MemoryInstructionsFragment => L["system.memoryInstructions"];

        #endregion

        #region Explore Agent Prompts

        /// <summary>
        /// Explore Agent 搜索指令 — 注入到探索任务的末尾，指导 AI 如何系统性地搜索代码库。
        /// </summary>
        public static string ExploreAgentInstructions => L["system.exploreAgentInstructions"];

        /// <summary>
        /// Explore Agent 仓库记忆指令 — 指导其复用并更新项目结构记忆。
        /// </summary>
        public static string ExploreMemoryInstructions => L["system.exploreMemoryInstructions"];

        #endregion

        #region Change Summary Prompts

        /// <summary>
        /// 代码变更总结 — 系统提示词（根据当前语言自动切换中英文）。
        /// </summary>
        public static string ChangeSummarySystemPrompt => L["system.changeSummarySystemPrompt"];

        /// <summary>
        /// 代码变更总结 — 用户指令（根据当前语言自动切换中英文）。
        /// </summary>
        public static string ChangeSummaryUserInstruction => L["system.changeSummaryUserInstruction"];

        #endregion

        #region AI Sub-prompts (summary polish, memory, auto-split, web search, workspace warning)

        /// <summary>自动记忆记录 — 系统提示词</summary>
        public static string MemoryAutoRecordSystemPrompt => L["system.aiPrompt.memoryAutoRecordSystem"];

        /// <summary>自动记忆记录 — 用户提示词（纯指令，基于完整上下文判断）</summary>
        public static string MemoryAutoRecordUserPrompt => L["system.aiPrompt.memoryAutoRecordUser"];

        /// <summary>项目外路径拒绝警告。{0}=targetPath, {1}=workspaceRoot</summary>
        public static string OutOfWorkspaceWarning => L["system.aiPrompt.outOfWorkspaceWarning"];

        /// <summary>
        /// 会话标题生成 — 提示词模板（{0}=用户消息，{1}=助手回复）。
        /// 标签文案随语言包翻译；分段换行由 <see cref="EnsureBreakAfterPlaceholders"/> 保证。
        /// </summary>
        public static string SessionTitlePromptTemplate => L["session.generateTitlePrompt"];

        #endregion

        #region Plan Agent Prompts

        /// <summary>
        /// Plan Agent 对齐检查 — 用户提示词模板。
        /// {0} = 用户消息
        /// {1} = 代码库发现上下文
        /// </summary>
        public static string PlanAlignmentCheckPrompt => L["system.planAlignmentCheckPrompt"];

        /// <summary>
        /// Plan Agent 对齐阶段 — 用户提示词模板。
        /// {0} = 用户消息
        /// </summary>
        public static string PlanAlignmentUserPrompt => L["system.planAlignmentUserPrompt"];

        #endregion

        #region Edit Agent Prompts

        /// <summary>
        /// Edit Agent 主体提示词；仅描述原生编辑工具工作流。
        /// </summary>
        public static string EditSystemPromptFragment => L["system.agent.editToolPromptFragment"];

        /// <summary>
        /// Edit Agent 步骤提示词前缀。
        /// {0} = 计划标题
        /// </summary>
        public static string EditStepPromptPrefix => L["system.editStepPromptPrefix"];

        /// <summary>
        /// Edit Agent 代码步骤的工具与验证约定。
        /// {0} = 当前阶段可用工具列表
        /// </summary>
        public static string EditCodeStepToolGuidance => L["system.editCodeStepToolGuidance"];

        #endregion

        #region Handoff & Context Prompts

        /// <summary>
        /// Handoff 身份边界提示 — 历史消息中的来源 Agent 身份不能覆盖当前模式。
        /// </summary>
        public static string HandoffRoleBoundaryPrompt => L["system.handoffRoleBoundaryPrompt"];

        /// <summary>
        /// 上下文压缩指令。待压缩内容由请求前缀携带。
        /// </summary>
        public static string CompressionPromptTemplate => L["system.compressionPromptTemplate"];

        /// <summary>
        /// 可导航引用格式化规则 — 追加到 SharedImmutablePrefix 末尾。
        /// </summary>
        public static string NavigableReferenceRule => L["system.navigableReferenceRule"];

        #endregion

        #region Common System Prompt Prefix

        /// <summary>
        /// 所有 Agent 共享的 System Prompt 前缀核心（不含语言指令和工具说明）。
        /// 放在 messages[0]，确保跨 Agent 切换时 DeepSeek Prefix Cache 仍能命中。
        /// </summary>
        public static string CommonSystemPromptPrefixCore => L["system.agent.commonSystemPromptPrefixCore"];

        /// <summary>
        /// 所有 Agent 共享的不可变前缀（含语言指令）。
        /// 始终放在 messages[0]，确保跨 Agent 切换时 DeepSeek Prefix Cache 永远命中。
        /// Agent 专属行为指令不在此前缀中，而是作为最后一条 system 消息注入。
        /// 
        /// 内容 = CommonSystemPromptPrefixCore（终端规则 + Handoff 规则）+ 语言指令
        ///        + 可导航引用规则。
        /// 
        /// 此属性同时被 BaseAgent.BuildContextAwareMessages（Agent 内部调用）和
        /// ConversationContextManager.BuildApiMessages（主流程 API 调用）使用，
        /// 确保两个路径的 messages[0] 完全一致，消除前缀漂移。
        /// </summary>
        public static string SharedImmutablePrefix => CommonSystemPromptPrefixCore + L["system.agent.languageInstruction"] + "\n"
            + L["system.navigableReferenceRule"] + "\n";

        #endregion

        #region Agent-Specific System Prompt Fragments

        /// <summary>工具调用输出规则 — 调用工具前用一句话预告工具结束后的下一步。</summary>
        public static string ToolCallOutputRule => L["system.agent.toolCallOutputRule"];

        /// <summary>所有 Agent 专属提示词共用的结论停止规则（含工具调用输出规则）</summary>
        public static string AgentConclusionStopRule =>
            L["system.agent.conclusionStopRule"] + "\n\n" + ToolCallOutputRule;

        /// <summary>Ask Agent — 代码库探索策略 + 记忆系统 + 移交规则</summary>
        public static string AskAgentPromptFragment => L["system.agent.askPromptFragment"];

        /// <summary>Explore Agent — 深度检索模式完整系统提示</summary>
        public static string ExploreAgentSystemPrompt =>
            L["system.agent.explorePrompt"] + "\n\n" + ExploreMemoryInstructions + AgentConclusionStopRule;

        /// <summary>Explore Agent — 定义描述</summary>
        public static string ExploreAgentDescription => L["system.agent.exploreDescription"];

        /// <summary>Explore Agent — 参数提示</summary>
        public static string ExploreAgentArgumentHint => L["system.agent.exploreArgumentHint"];

        /// <summary>Plan Agent — MCP 外部工具片段</summary>
        public static string PlanAgentMcpFragment => L["system.agent.planMcpFragment"];

        /// <summary>Build Agent — MCP 外部工具片段</summary>
        public static string BuildAgentMcpFragment => L["system.agent.buildMcpFragment"];

        /// <summary>Edit Agent — 验证阶段用户消息模板。{0}=changes summary, {1}=explore context, {2}=sanity warnings</summary>
        public static string EditVerifyUserMessage => L["system.agent.editVerifyUserMessage"];

        #endregion

        #region Helper Methods

        /// <summary>
        /// 构建会话标题生成提示词（用户消息部分）。
        /// <para>
        /// 历史缺陷：模板把「引导语 / 用户：/ 助手：/ 标题：」四段之间的换行全部写成
        /// JSON 转义（<c>\n\n</c>）。资源文件一旦缺了这个转义，渲染结果就退化成一段：
        /// <c>用户：&lt;问题&gt;助手：&lt;回复&gt;</c>——两个标签与各自内容互相粘连，
        /// 模型无法分辨「问题何时结束、回复何时开始」，直接损害标题质量。
        /// </para>
        /// <para>
        /// 修复方式：文案（含标签）仍留在语言包里以便翻译，但**分隔换行由本方法保证**——
        /// 每个占位符之后如果没有换行，就补一个空行。已有换行时保持原样（幂等），
        /// 因此资源文件无论怎么写都不会再退化成粘连。
        /// </para>
        /// </summary>
        /// <param name="userMessage">第一条用户消息（截断由调用方决定）。</param>
        /// <param name="assistantReply">第一条助手回复；为空时省略该段，避免出现空的助手标签。</param>
        /// <returns>可直接作为 user 消息发送的标题生成提示词。</returns>
        public static string BuildSessionTitlePrompt(string userMessage, string assistantReply)
        {
            string template = EnsureBreakAfterPlaceholders(SessionTitlePromptTemplate);
            return string.Format(template, userMessage ?? string.Empty, assistantReply ?? string.Empty);
        }

        /// <summary>
        /// 在 <c>{0}</c>/<c>{1}</c> 占位符之后补齐换行分隔（幂等）。
        /// <para>
        /// 占位符代表「上一条消息的正文」，其后必须换行才能与下一个标签分开；
        /// 已经是换行（含空行、含 \r\n）的模板不做任何改动。
        /// </para>
        /// </summary>
        internal static string EnsureBreakAfterPlaceholders(string template)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;

            // \{([01])\} 后面若没有换行（允许存在空格/制表符），补一个空行
            return System.Text.RegularExpressions.Regex.Replace(
                template,
                @"\{([01])\}(?![ \t]*(?:\r?\n|$))",
                "{$1}\n\n");
        }

        #endregion
    }
}
