# KazumiHelper build script
# 输出: dist\libmpv-2.dll (代理) + dist\KazumiHelper.exe (伴生工具)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# ---- 1) 代理 DLL (需要 VS C++ 工具链) ----
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "未找到 MSVC C++ 工具链 (需要 VS 的 Desktop development with C++)" }
$msvc = (Get-ChildItem "$vsPath\VC\Tools\MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"
$sdk = (Get-ChildItem "$sdkRoot\Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name

$env:PATH = "$msvc\bin\Hostx64\x64;$sdkRoot\bin\$sdk\x64;$env:PATH"
$env:INCLUDE = "$msvc\include;$sdkRoot\Include\$sdk\ucrt;$sdkRoot\Include\$sdk\um;$sdkRoot\Include\$sdk\shared;$sdkRoot\Include\$sdk\winrt;$sdkRoot\Include\$sdk\cppwinrt"
$env:LIB = "$msvc\lib\x64;$sdkRoot\Lib\$sdk\ucrt\x64;$sdkRoot\Lib\$sdk\um\x64"

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
cl /nologo /LD /O2 /W3 /utf-8 "$root\proxy.c" /Fo:"$root\dist\proxy.obj" /Fe:"$root\dist\libmpv-2.dll" /link /IMPLIB:"$root\dist\proxy.lib" /PDB:"$root\dist\libmpv-2.pdb"

# ---- 2) 伴生工具 (Windows 自带 csc, 无需安装任何东西) ----
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:"$root\dist\KazumiHelper.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "$root\kazumi_helper.cs"

Write-Host "`nBuild done -> $root\dist" -ForegroundColor Green
