# MVP-PowerShell 原型

这是微信消息朗读助手的第一版可运行原型，用于先验证 Windows 本机文字朗读能力。

## 当前功能

1. 使用 Windows 自带语音引擎朗读指定文字。
2. 支持语速、音量参数。
3. 提供测试脚本，确认电脑是否可以正常朗读中文。

## 运行方式

在 PowerShell 中进入本目录：

```powershell
cd D:\WeChatMessageReaderAssistant\src\MVP-PowerShell
.\scripts\Test-Speech.ps1
```

也可以指定朗读内容：

```powershell
.\scripts\Speak-Text.ps1 -Text "张三说，您好，这是一条测试微信消息。"
```

## 说明

当前电脑只有 .NET Runtime，没有 .NET SDK，所以暂时不能创建正式 C#/.NET 桌面项目。
本原型先验证核心能力：文字转语音。
后续安装 .NET SDK 后，再迁移到正式 C# WPF/WinUI 项目。
