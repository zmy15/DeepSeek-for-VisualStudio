using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using DeepSeek_v4_for_VisualStudio.Utils;
using DeepSeek_v4_for_VisualStudio.View;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services.Agents
{
    /// <summary>
    /// <see cref="BaseAgent"/> 的 公共提示词前缀 分部实现。
    /// 承载该职责域的成员，使主文件聚焦于 Agent 执行编排。
    /// </summary>
    public abstract partial class BaseAgent
    {
        /// <summary>
        /// 所有 Agent 共享的 System Prompt 前缀（不含语言指令，由 GetCommonSystemPromptPrefix 动态注入）。
        /// 放在 messages[0]，确保跨 Agent 切换时 DeepSeek Prefix Cache 仍能命中。
        /// 文本内容集中管理在 AiPrompts.CommonSystemPromptPrefixCore（通过 LocalizationService 支持多语言）。
        /// </summary>
        protected static string CommonSystemPromptPrefixCore => AiPrompts.CommonSystemPromptPrefixCore;

        /// <summary>
        /// 获取带语言指令的完整公共前缀（每次调用时根据当前语言动态拼接）。
        /// </summary>
        protected static string GetCommonSystemPromptPrefix()
        {
            return CommonSystemPromptPrefixCore + LocalizationService.Instance["system.agent.languageInstruction"] + "\n";
        }

        /// <summary>
        /// 所有 Agent 共享的不可变前缀。始终放在 messages[0]，确保跨 Agent 切换时
        /// DeepSeek Prefix Cache 永远命中。Agent 专属行为指令不在此前缀中，
        /// 而是作为最后一条 system 消息注入（位于历史之后、用户消息之前）。
        /// 
        /// 内容 = CommonSystemPromptPrefixCore（角色定义 + 文件规则 + 终端规则 +
        /// Handoff 规则）+ 语言指令。
        /// 
        /// 委托给 AiPrompts.SharedImmutablePrefix，确保与 ConversationContextManager
        /// 的 BuildApiMessages 使用完全相同的 messages[0] 内容。
        /// </summary>
        protected static string GetSharedImmutablePrefix()
        {
            return AiPrompts.SharedImmutablePrefix;
        }
    }
}
