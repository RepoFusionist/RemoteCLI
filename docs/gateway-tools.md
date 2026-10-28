# 网关对话工具

Remote CLI 自有网关对话向 OpenAI 兼容或 Anthropic 网关提供启用的工具定义，将执行结果送回模型，并在对话中展示调用和结果。一轮最多 8 个调用、一次请求最多 30 轮；超过限制会停止。工具在“设置 → 全局工具”中逐项启用，配置对所有自定义网关对话生效，本轮新增项默认关闭，原有选择保留。工作区工具要求选定有效的本地工作区。

实现参考了 [Codex 工具处理器](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/mod.rs)、[Codex 命令会话](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/unified_exec.rs)、[DeepSeek Harness 文件搜索](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/fs/tool-fs-search/README.md)、[PowerShell 工具](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/shell/tool-pwsh/README.md)和 [Exa 搜索提供方](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/web/web-search-exa/README.md)。这些是 Remote CLI 的实现，并未打包或启动上游 CLI/DSH 插件。

## 文件和补丁

- `list_files`、`read_file`、`search_text`：目录、文本读取和字面搜索；`read_file` 可用 `start_line`、`max_lines` 分页。
- `glob`、`grep`：复用远程端 `AgentFileTools` 的通配符和正则搜索实现。结果、文件大小和扫描数量有上限，非完整 ripgrep 语法兼容实现。
- `write_file`、`append_file`、`delete_file`、`edit_file`：文件修改；`edit_file` 要求旧文本恰好出现一次。启用后由模型调用，没有逐次审批。
- `apply_patch`：支持 Codex 的 Add、Update、Delete、Move、锚点、多修改块和 EOF 标记，保留 UTF-8 BOM 与 Windows 换行；每次显示完整补丁并由本机用户批准。文件在审批后被更改会拒绝执行。单文件写入先准备临时文件再替换；多文件提交不是事务，失败时结果会列出已应用的文件。仍不支持上游所有 Unicode 模糊匹配和 Environment ID。

文件操作检查工作区路径并拒绝现有符号链接或目录联接；路径检查与操作之间不是原子的，不能作为针对恶意本地并发进程的强隔离。

## 命令

启用“本机 PowerShell”后模型可用 `exec_command` 启动 Windows PowerShell 5.1，以及用 `write_stdin` 发送输入或读取后续输出；设置 `tty=true` 使用 Windows ConPTY，`columns`/`rows` 可调整尺寸。每次启动和每次非空输入都显示具体内容，用户批准后才执行。命令在选择的工作区开始，但**工作目录不是沙箱**：批准的命令仍可访问本机其他文件、进程和网络。每个对话最多四个并发命令；单个命令默认两分钟到期终止，可用 `timeout_ms` 配置为 1000–3600000 毫秒，输出受限。同一对话可跨用户轮次继续会话；取消请求、归档或删除对话、移除工作区或关闭应用会清理相应进程；超过五分钟未访问的对话会在后续申请终端时清理。

交互式命令的终端卡片使用随安装包提供的 xterm.js 做真实屏幕仿真：光标、清屏、滚动和颜色按 VT 序列还原，控制序列不会被执行成界面行为。模型读取工具输出与界面观看各自保留独立游标，互不吞掉数据；界面刷新或重开后恢复最后保存的屏幕，并把旧会话标为已断开，不再等待已结束的后端进程。

## 远程

启用“远程文件与工具”后，模型可用 `remote_tools_list` 发现已认证被控端的工具，通过 `remote_tool_call` 提交，使用 `remote_tool_receive` 按游标收取最终结果或 `remote_tool_cancel` 取消。被控端现有目录、文件搜索、正则搜索、读取、写入、移动、复制等工具通过原有注册表提供，具体可用工具取决于连接、权限和远端配置。

启用“远程命令”后，可用 `remote_command_start`、`remote_command_receive`、`remote_command_cancel`，沿用现有命令会话和被控端权限。两组功能都复用本机连接协调器，验证当前认证会话、权限和状态版本；会话更换后旧请求不会转发到新会话。远程操作由已有远程授权和审批规则约束，返回的远程内容应视为不可信。

## 网络

`web_search` 在“设置 → 全局工具 → 网络搜索服务”中选择 provider。自动模式在有 Exa 密钥时使用 Exa，否则尝试 DuckDuckGo HTML；DuckDuckGo 可能受网络、限流或页面格式变化影响。Exa 使用 `POST /search`、Bearer API key；DeepSeek 使用官方直接 Search API 的 `POST https://api.deepseek.com/v1/tools/search`，请求 `query`、结果数量和 `need_content`，不额外发起一次模型 Messages 请求。两者都支持每次查询前本机批准和可配置总超时，密钥分别来自设置、`EXA_API_KEY` 或 `DEEPSEEK_API_KEY`；DeepSeek 未单独填写密钥时会复用自定义网关密钥。搜索 provider 失败时不会把长时间等待重复到备用端点，超时预算覆盖整次搜索。

Codex CLI 的实现通过已认证的托管搜索 API 发出 `web.run` 请求；DeepSeek Harness 将模型工具与 provider 分开，并为 Exa、DeepSeek Search/Fetch API 提供 API key、base URL、结果上限、摘要和取消/错误分类。Remote CLI 采用相同的 provider 边界，但不会捆绑或启动上游 CLI/DSH。参考：[DeepSeek Search API](https://api-docs.deepseek.com/api/search/)、[DeepSeek Harness web-deepseek-api](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/web/web-deepseek-api)。

`web_fetch` 读取公开 HTTPS 文本或 HTML，限制大小和时间，禁止内网地址及自动重定向，不使用代理；HTML 是基础标签清理，不能等同浏览器呈现。

另有独立的 `web_search_exa` 工具：用户先启用“独立 Exa 搜索”，然后在同一设置中填写密钥或在启动 Remote CLI 的进程环境中提供 `EXA_API_KEY`；每次调用显示查询和配置的 `/search` 目的地，批准后才发送。密钥不进入工具参数或执行结果；取消或拒绝不发出请求。Exa 是外部服务，是否收费及可用性由该服务和用户账户决定。没有配置或未批准时返回明确错误。

## MCP 插件

在“设置 → 全局工具 → MCP 服务器”中配置 JSON 数组并单独启用服务器，支持 stdio 进程与 HTTPS 或本机回环 HTTP。`mcp_list_tools` 发现服务器及工具，`mcp_call_tool` 调用已发现工具；连接和每次调用都显示具体目标及参数，请求用户逐次批准。默认只向进程提供 SDK 的基础环境；stdio 的 `env`、HTTP 的 `headers` 可将指定本机环境变量映射到目标名称，配置只保存变量名。JSON 配置会明文持久化，勿将密钥放入 URL 或进程参数。服务器结果是不可信数据，插件默认无跨请求持久连接；当前模型接口只接入 MCP 工具，不提供 prompts/resources 或图片结果的完整多模态处理。

## 图片与子代理

网关对话可选择 PNG、JPEG、GIF、WebP 图片，每张最多 1 MiB、每次模型请求最多 4 张且合计不超过 3 MiB。图片随会话保存在本机，并发送给所选模型网关；模型必须支持图片输入。请求中的历史图片按最新优先保留在预算内。

启用子代理后可通过 `spawn_agent`、`wait_agent`、`cancel_agent` 执行独立任务：每次父请求最多 8 个子任务、最多 3 个并发，每个任务最多两分钟。子代理独立上下文只接收任务及启用的只读文件/网络工具，不继承写入、命令、MCP 或远程调用能力；父请求结束时取消子代理。

自动化测试使用模拟 Exa 响应，不代表用户的真实 Exa 凭据、网络和模型网关已经验证。Codex 独立反向会话继续沿用原有权限设置。项目没有接入所有上游插件和完整工具系统。
