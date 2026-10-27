# 客户端 AOT 与裁剪评估

日期：2026-10-27。实际 SDK：.NET 10.0.201；客户端为 Windows Forms + WebView2 + 内嵌 MCP，Windows x64 单文件。

当前客户端不能安全启用 Native AOT 或裁剪。已直接执行本机 SDK 的 `_CheckForUnsupportedAppHostUsage` 检查，传入 `PublishAot=true`、`PublishTrimmed=true`、`SelfContained=true`、`RuntimeIdentifier=win-x64`，得到 **NETSDK1175：启用裁剪时，不支持或不推荐使用 Windows 窗体**。对应检查位于 SDK 的 `Microsoft.NET.RuntimeIdentifierInference.targets` 第 305 行。Native AOT 依赖裁剪，因此该限制同时阻止当前结构的 AOT 发布。

本轮采用单文件压缩 `EnableCompressionInSingleFile=true`：保留运行时、UI、COM 和 MCP 所需的程序集，减少分发体积。具体文件体积、SHA-256 和发布版 MCP 验证结果见 `artifacts/latest-release.json`、`artifacts/codex-mcp-install-log.md` 与实施报告。没有关闭 SDK 的 WinForms 兼容性检查。

后续要采用 AOT，需要评估更换支持 AOT 的界面宿主，并完成 WebView2 COM、MCP 动态注册和 JSON 序列化的兼容性改造，不能把一次编译成功当成完整界面验证。

参考：[微软裁剪兼容性限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)、[Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[单文件压缩](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)。
