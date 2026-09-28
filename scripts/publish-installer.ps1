$ErrorActionPreference = "Stop"

function New-UnicodeString($codes) {
    return [string]::Concat([char[]]$codes)
}

$root = "D:\WeChatMessageReaderAssistant"
$portable = Join-Path $root "build\portable"
$work = Join-Path $root "build\selectable-installer"
$source = Join-Path $root "tools\installer\SelectableInstallFolderInstaller.cs"
$manifest = Join-Path $root "tools\installer\app.manifest"
$payload = Join-Path $work "payload.zip"

$installerFileName = New-UnicodeString @(0x7535,0x8111,0x7248,0x5fae,0x4fe1,0x6d88,0x606f,0x6717,0x8bfb,0x52a9,0x624b,0x2e,0x65,0x78,0x65)
$output = Join-Path "D:\" $installerFileName

if (-not (Test-Path -LiteralPath $portable)) {
    throw "Portable publish directory does not exist. Run scripts\publish-portable.ps1 first."
}
if (-not (Test-Path -LiteralPath (Join-Path $portable "WeChatMessageReaderAssistant.exe"))) {
    throw "Portable publish directory does not contain WeChatMessageReaderAssistant.exe."
}

if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $work | Out-Null

Compress-Archive -Path (Join-Path $portable "*") -DestinationPath $payload -CompressionLevel NoCompression -Force

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Could not find .NET Framework C# compiler csc.exe."
}

$compiled = Join-Path $work $installerFileName
$arguments = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/win32manifest:$manifest",
    "/resource:$payload,payload.zip",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.IO.Compression.dll",
    "/reference:System.IO.Compression.FileSystem.dll",
    "/reference:Microsoft.CSharp.dll",
    "/out:$compiled",
    $source
)

& $csc @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed."
}

Copy-Item -LiteralPath $compiled -Destination $output -Force

$main = Get-Item -LiteralPath $output
Write-Host ("Installer created: " + $main.FullName)
Write-Host ("Size bytes: " + $main.Length)
