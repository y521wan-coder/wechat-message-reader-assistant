$ErrorActionPreference = "Stop"
$root = "D:\WeChatMessageReaderAssistant"
$dotnet = Join-Path $root "tools\dotnet\dotnet.exe"
$project = Join-Path $root "src\WeChatMessageReaderAssistant\WeChatMessageReaderAssistant.App\WeChatMessageReaderAssistant.App.csproj"
$output = Join-Path $root "build\portable"

if (Test-Path $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $output `
    /p:PublishSingleFile=false `
    /p:EnableCompressionInSingleFile=true

$readme = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String("5b6u5L+h5raI5oGv5pyX6K+75Yqp5omLIC0g57u/6Imy54mICgrov5DooYzmlrnlvI/vvJoK55u05o6l5Y+M5Ye7IFdlQ2hhdE1lc3NhZ2VSZWFkZXJBc3Npc3RhbnQuZXhlIOWNs+WPr+i/kOihjOOAgui9r+S7tuWQr+WKqOWQjuS8muiHquWKqOW8gOWni+ebkeWQrOW+ruS/oe+8jOW5tuiHquWKqOmakOiXj+WIsOmAmuefpeWMuuWfn+WQjuWPsOi/kOihjOOAggoK5b2T5YmN54mI5pys5Yqf6IO977yaCjEuIOiHquWKqOacl+ivu+eUteiEkeeJiOW+ruS/oeW9k+WJjeiBiuWkqeeql+WPo+mHjOeahOaWsOaWh+Wtl+a2iOaBr+OAggoyLiDmlK/mjIHlpb3lj4vogYrlpKnlkoznvqTogYrnmoTmloflrZfmtojmga/mnJfor7vjgIIKMy4g5Y+q5pyX6K+75paH5a2X5raI5oGv77yb6K+t6Z+z44CB5Zu+54mH44CB6KGo5oOF44CB5paH5Lu2562J5raI5oGv5pqC5LiN5pyX6K+744CCCjQuIOacl+ivu+aWueW8j+m7mOiupOiHquWKqOajgOa1i+W9k+WJjeivu+Wxj++8jOS8muaMieS6iea4oeOAgeS/neebiuOAgU5WREEg55qE6aG65bqP5qOA5rWL5Y+v55So5o6l5Y+j77yb6YO95LiN5Y+v55So5pe26Ieq5Yqo5Zue6YCAIFdpbmRvd3MgU0FQSSDns7vnu5/or63pn7PlupPvvIzpgb/lhY3lvq7kv6Hmtojmga/ml6Dlo7DjgIIKNS4g5Lmf5Y+v5Lul5Zyo5Li756qX5Y+j5omL5Yqo6YCJ5oup5LqJ5rih5a6Y5pa55o6l5Y+j44CB5L+d55uK5LiJ5pa55pyX6K+75pyN5Yqh44CBTlZEQSDlrpjmlrnmjqfliLbmjqXlj6PmiJYgV2luZG93cyBTQVBJ44CCCjYuIOaUr+aMgemAmuefpeWMuuWfn+Wbvuagh+WQjuWPsOi/kOihjO+8jOWPs+mUruaIluiPnOWNlemUruWPr+aJk+W8gOS4u+eql+WPo+WSjOW4uOeUqOiuvue9ruOAggoK5bi455So5b+r5o236ZSu77yaCkFsdCtS77ya5rWL6K+V5pyX6K+744CCCkFsdCtT77ya5YGc5q2i5pyX6K+744CCCkFsdCtN77ya5byA5aeL5oiW5YGc5q2i5b6u5L+h55uR5o6n44CCCkFsdCtF77ya5pyX6K+75byV5pOO6K+K5pat44CCCkN0cmwrUe+8muecn+ato+mAgOWHuueoi+W6j+OAggoK6YCa55+l5Yy65Z+f5pON5L2c77yaCjEuIOaMiSBXaW5kb3dzK0Ig5Y+v56e75Yqo5Yiw6YCa55+l5Yy65Z+f44CCCjIuIOeUqOaWueWQkemUruaJvuWIsOKAnOW+ruS/oea2iOaBr+acl+ivu+WKqeaJi+KAneOAggozLiDmjInoj5zljZXplK7miJYgU2hpZnQrRjEwIOaJk+W8gOiPnOWNleOAggoK6ZSZ6K+v5oql5ZGK6K+05piO77yaCuWmguaenOi9r+S7tuWcqOaci+WPi+eUteiEkeS4iumBh+WIsOmUmeivr++8jOS8muWwvemHj+WcqOe7v+iJsueJiOeoi+W6j+aJgOWcqOebruW9leeUn+aIkO+8mumUmeivr+aKpeWRii50eHTjgIIK6K+36K6p5pyL5Y+L5oqK6L+Z5Liq5paH5Lu25Y+R57uZ5oKo77yM5oKo5YaN5Y+R57uZ5byA5Y+R6ICF5o6S5p+l5Y2z5Y+v44CCCgrms6jmhI/vvJoK5YWz6Zet5Li756qX5Y+j5oiW5oyJIEFsdCtGNCDml7bvvIznqIvluo/lj6rmmK/pmpDol4/liLDpgJrnn6XljLrln5/vvIzlubbkuI3kvJrpgIDlh7rjgIIK5aaC5p6c6KaB55yf5q2j6YCA5Ye677yM6K+35oyJIEN0cmwrUe+8jOaIluiAheWcqOmAmuefpeWMuuWfn+iPnOWNlemAieaLqeKAnOmAgOWHuueoi+W6j+KAneOAggoK5peg6Zqc56KN6K+05piO77yaCuacrOi9r+S7tumHjeinhuS6iea4oeOAgeS/neebiuOAgU5WREEg562J5bGP5bmV6ZiF6K+75Zmo5L2T6aqM44CC5Li756qX5Y+j54q25oCB44CB6YCa55+l6K6w5b2V5ZKM6K+K5pat5L+h5oGv6YO95Y+v5Lul5aSN5Yi244CC"))
$readmeName = -join ([char[]](0x7eff,0x8272,0x7248,0x8bf4,0x660e,0x2e,0x74,0x78,0x74))
[System.IO.File]::WriteAllText((Join-Path $output $readmeName), $readme, [System.Text.Encoding]::UTF8)
$msg = -join ([char[]](0x7eff,0x8272,0x7248,0x5df2,0x53d1,0x5e03,0x5230,0xff1a))
Write-Host ($msg + $output) -ForegroundColor Green
Get-ChildItem -Path $output | Select-Object Name,Length
