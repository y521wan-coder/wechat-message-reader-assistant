$ErrorActionPreference = "Stop"
$root = "D:\WeChatMessageReaderAssistant"
$dotnet = Join-Path $root "tools\dotnet\dotnet.exe"
$sln = Join-Path $root "src\WeChatMessageReaderAssistant\WeChatMessageReaderAssistant.sln"
& $dotnet build $sln -c Debug
