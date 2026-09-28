$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$speakScript = Join-Path $scriptDir "Speak-Text.ps1"

& $speakScript -Text "您好，微信消息朗读助手第一版原型已经启动。张三说，这是一条文字消息测试。李四给您发来一条语音消息。" -Rate 0 -Volume 100
