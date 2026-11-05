using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Utils;

public class FileSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(3 * 1024 * 1024 + 512 * 1024, "3.5 MB")]
    public void Format_UsesExpectedBinaryUnits(long bytes, string expected)
    {
        FileSizeFormatter.Format(bytes).Should().Be(expected);
    }
}
