# Windows 沙盒测试

Remote CLI 单文件包含 .NET 运行时，桌面界面另外依赖 Microsoft WebView2 Runtime。出现“Couldn't find a compatible WebView2 Runtime installation”时，需要在沙盒内安装该组件。主机已安装的运行组件不会自动出现在沙盒中。

## 准备与启动

先按仓库根目录的构建步骤生成客户端，再从微软官方下载 x64 WebView2 Runtime 离线安装程序。测试目录和 `.wsb` 配置由本机准备，不随源码分发。

已经打开的沙盒可通过主机复制、沙盒粘贴的方式传入整个测试目录。关闭报错的 Remote CLI，在沙盒中运行 `MicrosoftEdgeWebView2RuntimeInstallerX64.exe`，安装完成后重新启动 `RemoteCli.Desktop.exe`。离线安装程序不需要在沙盒中下载运行组件。

新开沙盒时，可在主机双击测试包中的 `Remote CLI-Test.wsb`。配置开启联网，并把测试目录只读映射为沙盒桌面的 `Remote CLI-Test-Files`；没有自动安装或启动命令。先把安装程序和客户端复制到沙盒的本地目录（例如 `C:\Remote CLI-Test`），再安装和启动。移动测试包或换电脑后，需将 `.wsb` 的 `HostFolder` 改为真实绝对路径。

## 主机与沙盒配对

1. 沙盒使用被控端，设置独立用户名，选择随机密码，开启连接授权。
2. 主机使用本机构建的 `RemoteCli.Desktop.exe`。MCP 通过进程发现和专用管道绑定服务，不要求相同 EXE 目录；多个服务可用 `remotecli_connection_discover` 的 `processId` 选择。
3. 主机输入沙盒显示的用户名和密码后连接，权限由被控端管理，主控端不再审批。两边默认中继地址均为 `https://api.sunxiaochuan258.de5.net/remotecli/`。
4. 等待两边显示“已进行连接”，再通过主机 MCP 查询状态和调用远端工具。只有 MCP 状态返回 `Connected` / `connected=true` 才表示远程会话可用。
5. 文件工具测试在沙盒内创建可写工作区，例如 `C:\Remote CLI-Workspace`，然后由被控端选择。只读映射目录不适合作为写入测试工作区。

关闭并丢弃沙盒会删除内部的软件、文件和状态；新会话需要重新安装 WebView2 并复制客户端。主机测试包会保留。

## 验证范围

构建脚本的自动化结果不能代替真实沙盒 UI 和配对测试。记录双方版本和实际连接状态；`ServiceUnavailable` 表示本地桌面服务不可用。被控端可动态切换工作区、权限、隔离限制及工具审批方式。

受限命令新增进程级资源上限：每条命令最多 16 个进程，进程树最多 1 GiB 提交内存与 50% 总处理器周期；每个服务进程同时最多两条隔离命令。单个工作区副本仍最多 512 MiB、20000 个文件。

参考：[WebView2 Runtime 部署](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)、[Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/threat-protection/windows-sandbox/windows-sandbox-overview)、[沙盒配置文件](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。
