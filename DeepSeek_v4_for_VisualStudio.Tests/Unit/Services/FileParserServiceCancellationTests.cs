using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

public class FileParserServiceCancellationTests
{
    [Fact]
    public async Task ParseFilesAsync_WhenCancelled_DoesNotStartParsing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = await FileParserService.ParseFilesAsync(
            new[] { "missing-file-1.txt", "missing-file-2.txt" },
            cts.Token);

        results.Should().BeEmpty();
    }
}
