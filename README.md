# 微信消息朗读助手

这是面向 Windows 盲人用户的微信消息朗读辅助程序。程序通过 UI Automation 读取当前微信聊天窗口中的新文字消息，支持争渡、保益、NVDA，失败时回退到 Windows SAPI。界面支持键盘与屏幕阅读器操作。

本仓库保存应用源码、构建脚本、必要的本机读屏接口文件、安装器源码、开发日志和交接说明。当前正式版的功能、快捷键及发布边界以根目录的《继续开发修改指导说明.txt》为准。此仓库没有附加开源许可证；公开可读不等于授予复制、修改或再分发许可。

## 源码与构建

- WPF 项目：`src/WeChatMessageReaderAssistant/WeChatMessageReaderAssistant.sln`
- 早期 PowerShell 原型：`src/MVP-PowerShell/`
- 构建脚本：`scripts/build.ps1`
- 绿色版发布脚本：`scripts/publish-portable.ps1`
- 安装器发布脚本：`scripts/publish-installer.ps1`
- 安装器源码：`tools/installer/`

构建脚本目前使用本机路径 `D:\WeChatMessageReaderAssistant\tools\dotnet\dotnet.exe` 中的 .NET 8 SDK。该 SDK 是可重新下载的工具，不属于源码备份。在另一台电脑构建时，需要安装 .NET 8 Windows Desktop SDK，并调整脚本中的 SDK 路径。发布脚本也使用固定的 `D:\WeChatMessageReaderAssistant` 路径。

## 隐私和本机文件

`config/`、`private/`、`build/`、下载工具、诊断扫描、安装包和用户已朗读历史不会上传。真实配置及用户数据留在本机。不要把凭证、签名私钥或聊天记录提交到公开仓库。

## 当前重要快捷键

`Ctrl+Shift+M`：全局暂停或恢复微信监听。`Ctrl+Q`：真正退出程序。其余快捷键和无障碍操作请看交接说明。