# Remote CLI

Remote CLI 提供 Windows 主控端、被控端和供 Agent 调用的 MCP 服务。两端经阿里云 WSS 中转配对，再建立经过密码证明和临时证书固定的 TLS 1.3 端到端通道。权限由被控端设置，主控端验证密码后直接连接。

仓库提供源码、测试、构建脚本和第三方许可声明。构建产物在本机生成；知识库、日志、服务器凭据和个人配置均不纳入版本控制。

客户端默认中继入口为 `https://api.sunxiaochuan258.de5.net/remotecli/`。服务端部署模板和构建步骤见 [部署说明](deploy/README.md)。实际服务器配置及云助手凭据由维护者在本机管理。

## 使用

桌面单文件自带 .NET，界面还需要 Microsoft WebView2 Runtime。干净虚拟机需安装微软官方 Runtime，步骤见 [Windows 沙盒测试](docs/windows-sandbox-testing.md)。

1. 被控端设置用户名，选择固定或随机密码，设置权限并开启连接授权。
2. 主控端输入被控端用户名和密码。双方校验密码证明并建立端到端 TLS，主控端不再审批权限。
3. 被控端随时调整“查看会话”“执行与取消命令”“工作区限制”，或选择新的工作区。变化立即同步到主控端和 MCP，并取消旧设置下仍在运行的操作。
4. 真实通道建立后两端显示“已进行连接”。本地服务、MCP、远程连接三盏灯分别反映自己的状态；HTML 独立预览不建立连接。

固定密码为 12–256 字符，首尾空格属于密码，经用户名绑定的 PBKDF2-SHA256 310,000 次迭代派生密钥。随机密码由 32 字节密码学随机数生成，显示为 43 字符 base64url。连接密码不提交给 Relay API。双方在 TLS 内使用新的随机挑战再次证明连接密钥，且邀请到期时间受认证保护；新旧握手版本不能互通，主控端和被控端须同时更新。当前证明协议不是 PAKE，弱固定密码仍存在离线猜测风险，建议使用随机密码。

## 长连接、权限和工作区

短授权期限只限制待连接阶段。配对后，中转使用 **90 秒空闲超时**；主控端每 **20 秒**发送加密心跳并核验应答。认证成功后不再被 9 分钟或原客户端 24 小时定时器切断。主动停止、断网、长期无应答和资源上限仍可结束连接。

查看会话控制会话信息及工具目录权限；执行和取消命令合并为一个开关，同时控制远端命令和工具调用。关闭全部操作权限后连接仍可保持，并可由被控端恢复。文件读写及安装工具默认按权限类别审批。被控端可开启自动批准，但每次启用都须在风险弹窗中确认；工作区和连接权限仍然生效。审批逐项显示，最多排队 16 个工具调用，取消或两分钟超时会清掉排队项并关闭对应弹窗。

连接中断后可通过“重新连接”发起新认证；不保存密码。随机密码已刷新时，主控端须输入被控端当前显示的密码。

工作区限制默认开启。文件工具拒绝越界、设备路径和符号链接；命令使用 Windows **LPAC AppContainer** 和关闭即终止的进程组。命令在专用临时存储中的工作区副本运行，使用相对路径；成功后核验源文件 SHA-256 冲突并同步变更，失败、超时或撤销时丢弃副本。源工作区的 ACL 和完整性标签不修改。副本上限为 512 MiB / 20000 文件；每个 Remote CLI 服务进程最多并行两条隔离命令，每个命令最多 16 个进程、768 MiB 单进程内存、1 GiB 进程树总提交内存和 50% 总处理器周期，隔离命令没有网络能力，必要系统运行组件仍由 Windows 加载。

关闭限制后，命令按被控端当前用户权限运行，文件工具可访问工作区外的本地路径。该开关只能由被控端更改，MCP 参数不能扩权。详见 [架构说明](docs/architecture.md)。

## MCP 与内置工具

本机网关对话使用原生 Git 工作树，需安装 Git。工作树默认位于所选工作目录的 `.remotecli/worktrees/`；已有仓库从当前提交 HEAD 创建会话分支，原目录未提交改动留在原处。文件工具、命令和文件预览使用会话工作树。回退通过切换到目标提交的新工作树完成，旧工作树及未提交内容保留；没有有效 Git 提交的节点显示灰色回退按钮。工作区回退不再使用 ZIP 或整目录快照。非 Git 工作目录首次使用时会初始化 Git 并提交初始文件；已有但尚无提交的仓库需先由用户提交。

同一 EXE 使用参数 `mcp` 提供 stdio 服务。MCP 发现运行中的 Remote CLI 进程，通过进程专用的当前用户管道连接，并核验实际服务 PID；桌面与 MCP 可以位于不同目录。启动 MCP 的 EXE 仍须存在，发布目录保留以供回滚。

- `remotecli_connection_discover`：发现服务及版本、状态，可传 `processId` 选择服务。会话期间保持绑定，服务退出后不会静默切换另一台远端。
- `remotecli_connection_status` / `wait`：查询实时状态或等待变化。只有 `Connected` / `connected=true` 表示远端可用，`ServiceUnavailable` 表示本地桌面服务不可用。
- `remotecli_connection_connect` / `reconnect`：请求桌面密码认证，可预填用户名。
- `remotecli_remote_command_start` / `receive` / `cancel`：提交命令、接收输出和最终结果、取消操作。
- `remotecli_remote_tools_list`、`remotecli_remote_tool_call` / `receive` / `cancel`：列出及参数化调用被控端工具。

| 内置工具 | 功能 |
| --- | --- |
| `remote.system_info` / `remote.workspace_info` | 系统信息、当前工作区和限制状态 |
| `remote.directory_list` / `remote.directory_create` | 分页浏览、创建目录 |
| `remote.file_search` / `remote.text_search` | 文件通配符搜索、文本或有界正则搜索 |
| `remote.file_read` / `remote.file_info` | 分页读取 UTF-8 文件、查看元数据 |
| `remote.file_create` / `remote.file_write` / `remote.file_edit` | 创建、带 SHA-256 覆盖校验、唯一文本替换 |
| `remote.file_copy` / `remote.file_move` / `remote.file_delete` | 复制、移动、删除文件 |

调用示例：向 `remotecli_remote_tool_call` 传入 `{"name":"remote.file_search","arguments":{"path":".","pattern":"**/*.cs"}}`，再用返回的 `operationId` 调用 `remotecli_remote_tool_receive`。提交成功不等于执行成功；最终失败、超时、取消会返回错误，不要为取回结果重复启动操作。

文件读取每页最多 2048 字符并返回整文件 SHA-256，工具结果最多 16 KiB。安装的 JSON 清单 + PowerShell 脚本工具也使用动态策略及命令隔离，安装后当前会话即可更新目录。

## 构建

需要 .NET SDK 10.0.201、Node.js（22.22.2+、24.15+ 或 26+ 的受支持版本）及 npm。在 Windows PowerShell 5.1 下运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\deploy-test-build.ps1
```

脚本完成页面脚本点击测试、完整 .NET 测试、Release 构建、压缩单文件发布、Codex MCP 注册和发布版验证，并在本机生成 `artifacts/latest-release.json`。发布文件位于 `artifacts/publish/`，不随源码提交。注册 MCP 需要已安装且可发现的 Codex CLI。

桌面项目使用单文件压缩和受限的程序集裁剪配置；运行时兼容设置见 [项目文件](src/RemoteCli.Desktop/RemoteCli.Desktop.csproj)。

## 源码目录

- `src/`：桌面、主控端、被控端、中继以及共享协议与安全实现。
- `tests/`：自动化测试及必要的文档样例。
- `tools/`：MCP 探针、UI 测试和代码高亮插件的构建源码。
- `scripts/`、`deploy/`：Windows 构建脚本及服务端部署模板。
- `ThirdPartyNotices/`：第三方许可文本，详见 [许可声明](THIRD-PARTY-NOTICES.txt)。
