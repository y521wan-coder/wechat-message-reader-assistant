# 微信消息朗读助手 WPF 项目

这是当前 Windows 桌面应用的源码目录。项目总览、MIT 许可证、第三方接口说明与最新开发交接资料见仓库根目录。

应用使用 UI Automation 读取电脑版微信当前聊天窗口的新文字消息，提供争渡、保益、NVDA 和 Windows SAPI 朗读方式。界面支持屏幕阅读器和全键盘操作。当前版本不会自动回复，不识别发言人或昵称，也不朗读图片、文件等非文字内容。

项目文件：`WeChatMessageReaderAssistant.sln`。构建命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\WeChatMessageReaderAssistant\scripts\build.ps1"
```

构建脚本使用本机 `D:\WeChatMessageReaderAssistant\tools\dotnet\dotnet.exe`，该 SDK 未纳入 Git 仓库。在新电脑上构建需先取得 .NET 8 Windows Desktop SDK，并按实际路径调整脚本。

重要操作：`Ctrl+Shift+M` 全局暂停或恢复微信监听，`Ctrl+Q` 真正退出。其余快捷键、用户正式配置及发布约束见根目录《继续开发修改指导说明.txt》。