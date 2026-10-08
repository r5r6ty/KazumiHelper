# KazumiHelper

不改官方 Kazumi 任何文件逻辑、不影响弹幕功能（dandanplay 凭证保留在官方 exe 内），为 Kazumi Windows 版添加逐帧播放与截图到剪贴板能力。

## 原理

Kazumi（media_kit fork）骑在 libmpv 上，但官方版没有暴露 IPC 控制口。本项目用两个小部件补上：

```
kazumi.exe（官方原版，弹幕照常）
  └─ libmpv-2.dll ← 代理 DLL（官方原 DLL 改名 libmpv-real.dll 保留）
       ├─ 拦截 mpv_client_api_version：
       │    media_kit fork 通过 native assets 解析出该函数地址后，
       │    用 GetModuleHandleExW(FROM_ADDRESS) 反查"mpv 所在模块"，
       │    再把所有函数直接绑到那个模块。让该函数留在代理里，
       │    反查即定位到代理，后续绑定全部经过代理。
       └─ 拦截 mpv_initialize：初始化前注入 input-ipc-server，
            开启命名管道 \\.\pipe\kazumi-mpv-ipc
KazumiHelper.exe（伴生托盘工具）
  └─ Kazumi 前台且播放中时接管 D/F/S，经管道发 mpv 命令
```

除 `libmpv-2.dll` 改名与放入代理外，不修改官方任何文件；52 个其余导出全部经链接器转发直达官方 DLL，零逐调用开销。

## 安装

1. 关闭 Kazumi，将 Kazumi 目录下的 `libmpv-2.dll` 改名为 `libmpv-real.dll`
2. 复制 `dist\libmpv-2.dll`（代理）到 Kazumi 目录
3. 运行 `KazumiHelper.exe`（托盘常驻）

## 热键（仅 Kazumi 前台且播放中生效）

| 键 | 功能 |
|----|------|
| F | 前进一帧 |
| D | 后退一帧 |
| S | 截图当前视频原始帧到剪贴板（视频原分辨率） |

- 带 Ctrl/Alt/Win 的组合键不受影响，Kazumi 不在前台时按键原样透传
- 托盘右键：临时停用热键、查看管道状态、退出
- 运行日志：`helper_log.txt`、`proxy_log.txt`

## 已知限制

- 逐帧后退对部分网络流（如 HLS 分段）可能受限，mpv 层的物理限制
- 直播流不支持逐帧
- 前台拦截无法感知 Kazumi 内部焦点：播放中在弹幕输入框打字时 D/F/S 会被接管，可从托盘临时停用热键

## 更新 Kazumi 后

官方升级覆盖安装目录后，重新执行安装第 1、2 步即可（代理对 libmpv 版本无要求，转发的是官方原 DLL）。

## 构建

PowerShell 运行 `build.ps1`，输出到 `dist\`：
- 代理 DLL 需要 VS 的 "Desktop development with C++" 工具链
- 伴生工具使用 Windows 自带的 .NET Framework csc，无需额外安装

## 目录结构

- `proxy.c` — libmpv 代理 DLL 源码（C/Win32）
- `kazumi_helper.cs` — 伴生工具源码（C#/WinForms）
- `ipc_test.ps1` — IPC 管道连通性自检脚本
- `build.ps1` — 一键构建
