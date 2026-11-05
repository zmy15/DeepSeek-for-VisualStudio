using System.Xml.Linq;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Commands;

/// <summary>
/// 命令表快捷键回归测试，防止全局快捷键被误改为编辑器局部作用域。
/// </summary>
public class CommandTableShortcutTests
{
    /// <summary>
    /// 打开聊天窗口的 Ctrl+Alt+Y 必须在 Visual Studio 全局作用域生效。
    /// </summary>
    [Fact]
    public void ShowChatWindow_UsesGlobalShortcutScope()
    {
        var commandTablePath = Path.Combine(AppContext.BaseDirectory, "VSCommandTable.vsct");
        var document = XDocument.Load(commandTablePath);

        var binding = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "KeyBinding"
                && element.Attribute("id")?.Value == "cmdidShowChatWindow");

        binding.Attribute("editor")?.Value.Should().Be("guidVSStd97");
        binding.Attribute("mod1")?.Value.Should().Be("Control Alt");
        binding.Attribute("key1")?.Value.Should().Be("Y");
    }
}
