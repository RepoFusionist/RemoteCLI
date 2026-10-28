# Remote CLI 图标

- `longshell-icon.svg`：可编辑的矢量源图。
- `longshell-icon.png`：1254×1254 RGBA 主图。
- `longshell-icon.ico`：包含 16、24、32、48、64、128、256 像素尺寸，供 Windows EXE 与窗口标题栏使用。

图形沿用深色圆角底，将参考草图整理为象牙白圆角线稿：左侧折线形成终端提示符，底部线路向上收尾，右上保留弧线和独立圆点。SVG 是设计源文件；`scripts\generate-icon-assets.ps1` 用 Windows PowerShell 5.1 和 GDI+ 重新生成 PNG 与多尺寸 ICO。

