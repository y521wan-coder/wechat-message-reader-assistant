param(
    [Parameter(Mandatory=$false)]
    [string]$Text = "您好，微信消息朗读助手测试成功。",

    [Parameter(Mandatory=$false)]
    [ValidateRange(-10, 10)]
    [int]$Rate = 0,

    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 100)]
    [int]$Volume = 100
)

$ErrorActionPreference = "Stop"

try {
    Add-Type -AssemblyName System.Speech
    $speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $speaker.Rate = $Rate
    $speaker.Volume = $Volume

    Write-Host "准备朗读：$Text" -ForegroundColor Cyan
    $speaker.Speak($Text)
    Write-Host "朗读完成。" -ForegroundColor Green
}
catch {
    Write-Host "朗读失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($speaker -ne $null) {
        $speaker.Dispose()
    }
}
