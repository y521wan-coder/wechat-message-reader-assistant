# WeChatMessageReaderAssistant

微信消息朗读与自动回复助手正式 C# 桌面项目。

## 当前状态

当前版本为第一版 WPF MVP：

- 可以输入文字并测试朗读；
- 可以测试“语音消息提示”；
- 支持语速和音量调节；
- 支持停止朗读；
- 尚未接入微信通知监听。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File "D:\WeChatMessageReaderAssistant\scripts\build.ps1"
```

## 运行

```powershell
powershell -ExecutionPolicy Bypass -File "D:\WeChatMessageReaderAssistant\scripts\run-app.ps1"
```

## 项目说明

本项目使用项目内置 .NET SDK：

```text
D:\WeChatMessageReaderAssistant\tools\dotnet\dotnet.exe
```

不依赖系统全局 .NET SDK。

## 无障碍说明

本项目面向盲人用户优化，当前窗口已加入：

- 全键盘可操作；
- 合理 Tab 顺序；
- 控件无障碍名称和说明；
- 状态区域 LiveRegion 提示；
- 快捷键：Alt+R 测试朗读，Alt+V 语音消息提示，Alt+S 停止朗读，Ctrl+Q 退出。

后续新增功能也必须保持无障碍兼容。
