$ErrorActionPreference = "Stop"
$root = "D:\WeChatMessageReaderAssistant"
$dotnet = Join-Path $root "tools\dotnet\dotnet.exe"
$project = Join-Path $root "src\WeChatMessageReaderAssistant\WeChatMessageReaderAssistant.App\WeChatMessageReaderAssistant.App.csproj"
& $dotnet run --project $project -c Debug
