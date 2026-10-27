# LongShell 被控端工具包

LongShell 的远程工具统一通过 `longshell_remote_tools_list`、`longshell_remote_tool_call`、
`longshell_remote_tool_receive` 和 `longshell_remote_tool_cancel` 调用。工具运行在被控端，
安装与首次调用审批也由被控端用户完成。自动批准默认关闭；被控端用户可在桌面端启用，每次启用会显示风险确认。队列最多接受 16 个调用，单次审批依次显示；取消或超时会清理等待项。

## 内置工具

- `remote.system_info`：读取有限的系统与 .NET 运行时信息。
- `remote.file_read`：读取用户所选工作区中的 UTF-8 文本，并返回 SHA-256。
- `remote.file_create`：创建 UTF-8 文本文件，不覆盖已有文件。
- `remote.file_edit`：按 `expectedSha256` 检查并替换唯一匹配文本，防止覆盖并发修改。

文件工具不接受绝对路径，也不穿越工作区或跟随重解析点。单次参数和结果均限制为 16 KiB。

## 安装工具

一个工具包由同目录的 JSON 清单和 `.ps1` 脚本组成。被控端在“AI 工具”区域点击“安装”，
选择清单文件。LongShell 使用 Windows PowerShell 5.1 解析器检查脚本，再复制到当前用户的
`%LOCALAPPDATA%\LongShell\Tools`。新工具从下一次远程会话开始出现在目录中。

清单示例：

```json
{
  "id": "text_length",
  "version": "1.0.0",
  "description": "Counts characters in supplied text.",
  "script": "text-length.ps1",
  "permissionDescription": "运行已安装的文本长度工具",
  "inputSchema": {
    "type": "object",
    "properties": { "text": { "type": "string", "maxLength": 8000 } },
    "required": ["text"],
    "additionalProperties": false
  },
  "outputSchema": {
    "type": "object",
    "properties": { "length": { "type": "integer" } },
    "required": ["length"],
    "additionalProperties": false
  }
}
```

脚本必须兼容 Windows PowerShell 5.1，接收 Base64 编码的 UTF-8 JSON 参数，并向标准输出
写入一个 JSON 值：

```powershell
param([Parameter(Mandatory = $true)][string]$ArgumentsBase64)

$json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
$inputObject = $json | ConvertFrom-Json
@{ length = ([string]$inputObject.text).Length } | ConvertTo-Json -Compress
```

脚本退出码必须为零，运行上限为 30 秒，标准输出必须是一个不超过 16 KiB 的 JSON 值。
