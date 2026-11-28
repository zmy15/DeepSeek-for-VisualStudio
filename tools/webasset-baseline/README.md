# WebAsset 迁移基线（静态 JS）

本目录保存**外移当刻**各 JS 构建方法的返回值原文，作为「行为等价」的逐字回归护栏。

- 每个 `*.baseline.js` 对应 `Resources\Chat\Js\` 下同名资源。
- 测试 `ChatHtmlServiceTests.StaticJsAsset_MatchesMigrationBaseline` 断言
  `WebAssetLoader.GetJs(短名)` 与本目录基线**逐字相等**（含首尾换行）。
- 基线内容反映外移时点的 C# 原始字符串值（`""` 已还原为 `"`）。
  若有意修改脚本正文，必须同步更新对应基线文件，否则测试会拦住这次改动——
  这正是该机制的目的：让脚本的每一次文本变化都显式可审。
