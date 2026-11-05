namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归测试：识别「同一解决方案被重载」。
/// <para>
/// 缺陷背景：VS 在 .sln/.slnx/.csproj 被外部修改后会先 close 再 open 同一个解决方案。
/// 旧实现不比较路径，一律按「切换解决方案」处理——保存 → 清空内存 → 从磁盘重载。
/// 当 Agent 正在执行时，界面因此回滚到任务执行前的快照，而后台任务仍在继续。
/// </para>
/// <para>
/// 判定必须容忍 VS 与 DTE/IVsSolution 之间可能存在的书写差异：
/// 大小写、末尾目录分隔符、相对片段。任一失败都会让修复失效（回到错误的重载路径）。
/// </para>
/// </summary>
public class SolutionIdentityTests
{
    [Theory]
    [InlineData(@"F:\CSharp\exp1\exp1.slnx", @"F:\CSharp\exp1\exp1.slnx")]
    [InlineData(@"F:\CSharp\exp1\exp1.slnx", @"F:\CSharp\exp1\exp1.slnx\")]
    [InlineData(@"F:\CSharp\exp1\exp1.slnx", @"f:\csharp\exp1\EXP1.SLNX")]
    [InlineData(@"F:\CSharp\exp1\", @"F:\CSharp\exp1")]
    [InlineData(@"F:\CSharp\exp1\.\exp1.slnx", @"F:\CSharp\exp1\exp1.slnx")]
    public void IsSame_SameSolutionWrittenDifferently_ReturnsTrue(string left, string right)
    {
        SolutionIdentity.IsSame(left, right).Should().BeTrue(
            $"'{left}' 与 '{right}' 指向同一个解决方案");
    }

    [Theory]
    [InlineData(@"F:\CSharp\exp1\exp1.slnx", @"F:\CSharp\exp2\exp1.slnx")]
    [InlineData(@"F:\CSharp\exp1\exp1.slnx", @"F:\CSharp\exp1\exp1.sln")]
    [InlineData(@"F:\CSharp\exp1", @"F:\CSharp\exp1\exp1.slnx")]
    public void IsSame_DifferentSolution_ReturnsFalse(string left, string right)
    {
        SolutionIdentity.IsSame(left, right).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, @"F:\CSharp\exp1\exp1.slnx")]
    [InlineData(@"F:\CSharp\exp1\exp1.slnx", null)]
    [InlineData("", "")]
    [InlineData("   ", @"F:\CSharp\exp1\exp1.slnx")]
    public void IsSame_AnySideEmpty_ReturnsFalse(string? left, string? right)
    {
        // 「没有解决方案」不代表「同一个解决方案」：关闭状态下的 open 必须走完整加载
        SolutionIdentity.IsSame(left, right).Should().BeFalse();
    }

    [Fact]
    public void Normalize_DriveRoot_KeepsTrailingSeparator()
    {
        // "C:" 是「当前目录」语义，不能由 "C:\" 去尾分隔符得到
        SolutionIdentity.Normalize(@"C:\").Should().Be(@"C:\");
    }

    [Fact]
    public void Normalize_TrimsWhitespace()
    {
        SolutionIdentity.Normalize("  F:\\CSharp\\exp1\\exp1.slnx  ")
            .Should().Be(@"F:\CSharp\exp1\exp1.slnx");
    }

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        SolutionIdentity.Normalize(null).Should().BeEmpty();
        SolutionIdentity.Normalize("").Should().BeEmpty();
    }
}
