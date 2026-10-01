using System.Text.Json;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class DeepSeekModelsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void DeepSeekChatRequest_Serialize_ProducesValidJson()
    {
        var request = new DeepSeekChatRequest
        {
            Model = "deepseek-v4-pro",
            Messages = new List<ChatApiMessage>
            {
                new() { Role = "user", Content = "Hello" }
            },
            Stream = true,
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);

        json.Should().Contain("\"model\"");
        json.Should().Contain("\"messages\"");
        json.Should().Contain("\"stream\"");
    }

    [Fact]
    public void DeepSeekChatRequest_WithThinking_SerializesCorrectly()
    {
        var request = new DeepSeekChatRequest
        {
            Model = "deepseek-v4-pro",
            Messages = new List<ChatApiMessage>(),
            Stream = true,
            Thinking = new ThinkingControl { Type = "enabled" },
            ReasoningEffort = "high",
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);

        json.Should().Contain("\"thinking\"");
        json.Should().Contain("\"reasoning_effort\"");
        json.Should().Contain("\"enabled\"");
    }

    [Fact]
    public void DeepSeekChatRequest_WithTools_SerializesCorrectly()
    {
        var request = new DeepSeekChatRequest
        {
            Model = "deepseek-v4-pro",
            Messages = new List<ChatApiMessage>(),
            Stream = true,
            Tools = new List<ToolDefinition>
            {
                new()
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "read_file",
                        Description = "Read a file",
                        Parameters = new { type = "object", properties = new { } }
                    }
                }
            },
            ToolChoice = "auto",
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);

        json.Should().Contain("\"tools\"");
        json.Should().Contain("\"tool_choice\"");
        json.Should().Contain("\"read_file\"");
    }

    [Fact]
    public void ChatApiMessage_WithToolCalls_SerializesCorrectly()
    {
        var message = new ChatApiMessage
        {
            Role = "assistant",
            Content = null,
            ToolCalls = new List<ToolCall>
            {
                new()
                {
                    Id = "call_123",
                    Type = "function",
                    Function = new ToolCallFunction
                    {
                        Name = "grep_search",
                        Arguments = "{\"pattern\":\"test\"}",
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(message, JsonOpts);

        json.Should().Contain("\"tool_calls\"");
        json.Should().Contain("\"call_123\"");
    }

    [Fact]
    public void ChatApiMessage_WithReasoningContent_SerializesWhenPresent()
    {
        var message = new ChatApiMessage
        {
            Role = "assistant",
            Content = "Here is the answer",
            ReasoningContent = "Let me think about this...",
        };

        var json = JsonSerializer.Serialize(message, JsonOpts);

        json.Should().Contain("\"reasoning_content\"");
        json.Should().Contain("Let me think about this");
    }

    [Fact]
    public void DeepSeekChatResponse_Deserialize_ReturnsValidObject()
    {
        var json = @"{
            ""id"": ""chatcmpl-123"",
            ""choices"": [
                {
                    ""index"": 0,
                    ""message"": {
                        ""role"": ""assistant"",
                        ""content"": ""Hello, World!""
                    },
                    ""finish_reason"": ""stop""
                }
            ],
            ""usage"": {
                ""prompt_tokens"": 10,
                ""completion_tokens"": 5,
                ""total_tokens"": 15
            }
        }";

        var response = JsonSerializer.Deserialize<DeepSeekChatResponse>(json, JsonOpts);

        response.Should().NotBeNull();
        response!.Id.Should().Be("chatcmpl-123");
        response.Choices.Should().HaveCount(1);
        response.Choices[0].Message!.Content.Should().Be("Hello, World!");
        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(10);
        response.Usage.CompletionTokens.Should().Be(5);
    }

    [Fact]
    public void DeepSeekUsage_Deserialize_WithCacheInfo()
    {
        var json = @"{
            ""prompt_tokens"": 100,
            ""completion_tokens"": 50,
            ""total_tokens"": 150,
            ""prompt_cache_hit_tokens"": 80,
            ""prompt_cache_miss_tokens"": 20
        }";

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage.Should().NotBeNull();
        usage!.PromptCacheHitTokens.Should().Be(80);
        usage.PromptCacheMissTokens.Should().Be(20);
        usage.PromptTokens.Should().Be(100);
    }

    // ── OpenAI 语义 usage（prompt_tokens_details.cached_tokens）兼容 ──
    // 回归：第三方网关走 OpenAI 命名，只报命中量、不报 prompt_cache_miss_tokens。
    // 修复前 miss 被当成 0，命中率虚报为 100%、未命中 token 按命中价计费。
    [Fact]
    public void DeepSeekUsage_OpenAiCachedTokens_DerivesMissFromPromptTotal()
    {
        // 实测抓包帧：vLLM 风格端点，hit+miss 字段缺席，只有 cached_tokens。
        var json = """
        {
            "prompt_tokens": 14276,
            "completion_tokens": 115,
            "total_tokens": 14391,
            "prompt_tokens_details": { "cached_tokens": 8192 },
            "reasoning_tokens": 21
        }
        """;

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage.Should().NotBeNull();
        usage!.PromptCacheHitTokens.Should().Be(0);          // 原生命名为空
        usage.EffectiveHitTokens.Should().Be(8192);          // 经别名归一
        usage.EffectiveMissTokens.Should().Be(6084);         // 守恒回退 = 14276 - 8192
        usage.CacheHitRate.Should().BeApproximately(0.5738, 0.0001);
        usage.CacheHitRatePercent.Should().Be("57.4%");
        usage.UsesCompatCacheFields.Should().BeTrue();       // 标注非原生命名来源
    }

    [Fact]
    public void DeepSeekUsage_OpenAiCachedZero_TreatsAllAsMiss()
    {
        var json = """
        {
            "prompt_tokens": 1000,
            "completion_tokens": 10,
            "prompt_tokens_details": { "cached_tokens": 0 }
        }
        """;

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage!.EffectiveHitTokens.Should().Be(0);
        usage.EffectiveMissTokens.Should().Be(0);            // 无命中即无可缓存量，不虚报
        usage.CacheHitRate.Should().Be(0);
    }

    [Fact]
    public void DeepSeekUsage_NativeFields_TakePrecedenceOverAlias()
    {
        // 同时出现两种命名时以 DeepSeek 原生字段为准。
        var json = """
        {
            "prompt_tokens": 100,
            "completion_tokens": 5,
            "prompt_cache_hit_tokens": 80,
            "prompt_cache_miss_tokens": 20,
            "prompt_tokens_details": { "cached_tokens": 12 }
        }
        """;

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage!.EffectiveHitTokens.Should().Be(80);
        usage.EffectiveMissTokens.Should().Be(20);
        usage.CacheHitRate.Should().BeApproximately(0.80, 0.0001);
    }

    [Fact]
    public void DeepSeekUsage_UnknownCacheFieldNames_MatchedBySuffix()
    {
        // 容错网：网关把字段改名成 *_cached_tokens / *_hit_tokens 也能识别。
        var json = """
        {
            "prompt_tokens": 500,
            "completion_tokens": 7,
            "prompt_cached_tokens": 400
        }
        """;

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage!.EffectiveHitTokens.Should().Be(400);
        usage.EffectiveMissTokens.Should().Be(100);
        usage.CacheHitRate.Should().BeApproximately(0.80, 0.0001);
    }

    [Fact]
    public void DeepSeekUsage_NoCacheInfo_ReportsZeroRate()
    {
        var json = """
        {
            "prompt_tokens": 200,
            "completion_tokens": 3
        }
        """;

        var usage = JsonSerializer.Deserialize<DeepSeekUsage>(json, JsonOpts);

        usage!.EffectiveHitTokens.Should().Be(0);
        usage.EffectiveMissTokens.Should().Be(0);
        usage.CacheHitRate.Should().Be(0);
    }

    [Fact]
    public void ThinkingControl_DefaultType_IsEnabled()
    {
        var control = new ThinkingControl();

        control.Type.Should().Be("enabled");
    }

    [Fact]
    public void ChatMessage_IsIncomplete_RoundTripsWithSystemTextJson()
    {
        var message = new ChatMessage
        {
            Role = "assistant",
            Content = "被停止的部分回复",
            IsIncomplete = true,
        };

        var json = JsonSerializer.Serialize(message);
        json.Should().Contain("\"IsIncomplete\":true");

        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json, JsonOpts);

        deserialized.Should().NotBeNull();
        deserialized!.IsIncomplete.Should().BeTrue();
    }

    [Fact]
    public void ChatMessage_LegacyJson_MissingIsIncomplete_DefaultsToFalse()
    {
        // 模拟旧版会话 JSON（无 isIncomplete 字段）：反序列化后应默认为 false
        const string legacyJson = @"{""role"":""assistant"",""content"":""已完成的回复""}";

        var message = JsonSerializer.Deserialize<ChatMessage>(legacyJson, JsonOpts);

        message.Should().NotBeNull();
        message!.IsIncomplete.Should().BeFalse();
    }
}
