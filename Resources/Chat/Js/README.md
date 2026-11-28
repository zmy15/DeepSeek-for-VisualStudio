# WebView 聊天页脚本资源目录
#
# 此处 *.js 通过 csproj 的 EmbeddedResource 通配项随程序集分发，
# 由 Services\WebAssets\WebAssetLoader.GetJs(name) 按短名读取（短名 = 文件名去扩展名）。
# 含运行时文案的脚本使用 {{key}} 占位符，取值由 C# 侧构造字典并在加载时注入。
# 本目录下文件应保持 UTF-8 无 BOM，且不得出现字面量 "{{"。
