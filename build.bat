@echo off
chcp 936 >nul
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [错误] 找不到 csc.exe，需要 Windows 自带的 .NET Framework 4.x
  pause
  exit /b 1
)

echo 正在编译 死亡打字员单词编辑器 ...
"%CSC%" /nologo /target:winexe /codepage:65001 /platform:anycpu /optimize+ ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:"死亡打字员单词编辑器.exe" WordEditor.cs

if errorlevel 1 (
  echo.
  echo [失败] 编译出错
  pause
  exit /b 1
)

echo.
echo [完成] 已生成 死亡打字员单词编辑器.exe
echo.
echo 正在自检（解码 488 个文件再编码，应逐字节一致）...
"死亡打字员单词编辑器.exe" --selftest "%~dp0.."
echo 自检结果写在 selftest-result.txt
echo.
pause
