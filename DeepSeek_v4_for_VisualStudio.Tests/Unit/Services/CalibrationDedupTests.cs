using DeepSeek_v4_for_VisualStudio.Services;
using FluentAssertions;
using Xunit;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归：同一份 API usage 常被两条链路各上报一次——
///   Services/Agents/BaseAgent.cs:1510        工具循环内按轮上报
///   View/DeepSeekChatControl.xaml.cs:1368    UI 收尾 RefreshConsumptionDisplay 上报
/// 二者共享 _apiService.LastUsage，prompt_tokens 完全相同。
///
/// 修复前 CalibrateFromApiUsage 内部无去重，同一 ratio 会被 EMA（α=0.15）连乘两次：
///   old=1.424219 → ratio=1.316090 → 1.408 → 1.394
/// 实测日志正为此值对（00:18:21.030 / .054，间隔 24ms）。
///
/// 断言方式：_calibrationFactor 私有，但 EstimatedTokens = rawEstimate × factor 公开可观测，
/// 故通过它反推因子，避免依赖反射。测试不假设 EstimateTokens 的具体取整规则，
/// 而是先用一次已知调用测出「单次应用」的因子，再验证重复上报不改变该值。
/// </summary>
public class CalibrationDedupTests
{
    private const double Alpha = 0.15;

    /// <summary>
    /// 构造一个 rawEstimate 足够大的 manager。
    /// 必要性：EstimatedTokens 为 int 截断（= (int)(raw × factor)），若 raw 很小
    /// （如单个短消息仅 ~13），因子变化会被截断吃掉，导致断言无法观测。
    /// 实测 20000 个 ASCII 字符 → raw = 6001，足以观测 1e-5 量级的因子差。
    /// </summary>
    private static ConversationContextManager CreateWithKnownEstimate(out int rawEstimate)
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage(new string('a', 20000));
        rawEstimate = mgr.RawEstimatedTokens;
        rawEstimate.Should().BeGreaterThan(5000, "raw 估算需足够大以免被 int 截断掩盖因子变化");
        return mgr;
    }

    private static double FactorOf(ConversationContextManager mgr, int rawEstimate)
        => (double)mgr.EstimatedTokens / rawEstimate;

    [Fact]
    public void SamePromptTokens_Twice_AppliedOnlyOnce()
    {
        var mgr = CreateWithKnownEstimate(out int raw);
        long promptTokens = raw * 2;

        mgr.CalibrateFromApiUsage(promptTokens);
        double afterFirst = FactorOf(mgr, raw);

        mgr.CalibrateFromApiUsage(promptTokens); // 同一份 usage 的第二次上报
        double afterSecond = FactorOf(mgr, raw);

        afterFirst.Should().NotBeApproximately(1.0, 1e-9, "首次上报应确实生效");
        afterSecond.Should().Be(afterFirst,
            "同一 prompt_tokens 属于同一次 API 调用，重复上报不得再次叠加 EMA");
    }

    [Fact]
    public void DuplicateReport_DoesNotCompoundEma()
    {
        var mgr = CreateWithKnownEstimate(out int raw);
        long promptTokens = raw * 5; // 落差大，便于观察过冲

        mgr.CalibrateFromApiUsage(promptTokens);
        mgr.CalibrateFromApiUsage(promptTokens);

        double actual = FactorOf(mgr, raw);
        double ratio = (double)promptTokens / raw;

        // 单次应用的理论值（old=1.0）
        double single = 1.0 * (1 - Alpha) + ratio * Alpha;
        // 若被连乘两次会得到的值
        double doubled = single * (1 - Alpha) + ratio * Alpha;

        actual.Should().BeApproximately(single, single * 0.01,
            "重复上报后应等价于仅应用一次");
        actual.Should().NotBeApproximately(doubled, doubled * 0.01,
            "修复前会被 EMA 连乘两次，因子被过度拉向单次观测值");
    }

    [Fact]
    public void DifferentPromptTokens_EachApplied()
    {
        var mgr = CreateWithKnownEstimate(out int raw);

        mgr.CalibrateFromApiUsage(raw * 2);
        double afterFirst = FactorOf(mgr, raw);

        mgr.CalibrateFromApiUsage(raw * 3); // 新一轮，prompt_tokens 不同
        double afterSecond = FactorOf(mgr, raw);

        afterSecond.Should().BeGreaterThan(afterFirst,
            "不同调用应各自参与校准，去重不得误伤正常轮次");
    }

    [Fact]
    public void DuplicateReport_LogsOnlyOnce()
    {
        var mgr = CreateWithKnownEstimate(out int raw);

        // 通过因子是否变化判断第二次是否被真正应用（不依赖日志抓取）
        mgr.CalibrateFromApiUsage(raw * 2);
        double f1 = FactorOf(mgr, raw);
        mgr.CalibrateFromApiUsage(raw * 2);
        double f2 = FactorOf(mgr, raw);

        f2.Should().Be(f1, "第二次上报应被直接忽略，不产生新的 EMA 更新");
    }

    [Fact]
    public void OutOfRangeRatio_DoesNotConsumeDedupSlot()
    {
        var mgr = CreateWithKnownEstimate(out int raw);

        // ratio 远超上限 10，应被过滤且不占用去重标记
        long abnormal = raw * 100;
        mgr.CalibrateFromApiUsage(abnormal);
        FactorOf(mgr, raw).Should().BeApproximately(1.0, 1e-6, "异常值不得参与校准");

        // 让估算增长后，同一数值落入合理区间，应能正常参与校准
        mgr.AddUserMessage(new string('x', raw * 50));
        int grown = mgr.RawEstimatedTokens;
        mgr.CalibrateFromApiUsage(abnormal);

        FactorOf(mgr, grown).Should().NotBeApproximately(1.0, 1e-6,
            "被过滤的异常值不应占用去重标记而挡掉后续合法校准");
    }

    [Fact]
    public void Clear_ResetsDedup_SoNewSessionCalibrates()
    {
        var mgr = CreateWithKnownEstimate(out int raw);
        long promptTokens = raw * 2;
        mgr.CalibrateFromApiUsage(promptTokens);
        FactorOf(mgr, raw).Should().NotBeApproximately(1.0, 1e-6);

        mgr.Clear();

        // 新会话重新累积相同规模的估算，并沿用同一 prompt_tokens 数值
        mgr.AddUserMessage(new string('a', 20000));
        int newRaw = mgr.RawEstimatedTokens;
        newRaw.Should().Be(raw, "重建同规模上下文以复用同一 prompt_tokens 数值");

        mgr.CalibrateFromApiUsage(promptTokens);

        FactorOf(mgr, newRaw).Should().NotBeApproximately(1.0, 1e-6,
            "Clear() 后去重标记应复位，新会话的首个 usage 需正常参与校准");
    }

    [Fact]
    public void NewManager_StartsUncalibrated()
    {
        var mgr = CreateWithKnownEstimate(out int raw);
        FactorOf(mgr, raw).Should().BeApproximately(1.0, 1e-6, "未收到 usage 前因子应为 1.0");
    }
}