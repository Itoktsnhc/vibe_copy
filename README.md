# VibeCopy

[![VirusTotal](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/Itoktsnhc/vibe_copy/main/.github/vt-badge.json)](https://www.virustotal.com/gui/file/0000000000000000000000000000000000000000000000000000000000000000)

Windows GUI 工具：相机 / 读卡器插入后，把多个可移动盘的照片、视频一次性归档到目标目录下的 `yyyy-MM-dd` 子文件夹，完成后一键弹出。

- 单文件 exe（约 19 MB，无需安装 .NET）
- 品牌无关：默认覆盖 Sony / Canon / Nikon / Fuji / Panasonic RAW + 常见视频
- 修改设置即时保存到 exe 同目录的 `vibecopy.config.json`
- 复制细节追加到 `vibecopy.log`
- 可选 SHA1 校验（复制全部完成后统一校验，避免读写混合）

## 系统要求

- **Windows 10 版本 1607 (x64)** 或更高
- Windows 11 / Windows Server 2016+ 均可

（基于 .NET 8 + Avalonia，最低系统要求即 .NET 8 官方要求。）

## 使用

1. 从 [Releases](../../releases) 下载 `VibeCopy.exe`
2. 双击运行（首次运行会在 exe 同目录生成 `vibecopy.config.json` / `vibecopy.log`）
3. 插入相机或 SD 卡
4. 选目标目录 → 勾选盘 → 「开始复制」

设置项：
- **扩展名**：逗号分隔，加机型直接改字符串
- **扫描子目录**：默认 `DCIM,PRIVATE,M4ROOT,XDROOT,MISC,AVCHD,CLIP,SSP`；留空则全盘扫
- **子文件夹规则**：按 `creation`（Windows 文件创建时间）或 `modified`（写入时间）分日期
- **同名冲突**：`skip` / `rename` / `overwrite`
- **复制后校验 (SHA1)**：额外一遍读校验，慢一倍
- **完成后自动弹出**：无错时安全弹出所有勾选盘

## 安全性

每个 Release 的 `VibeCopy.exe` 由 GitHub Actions 在 `windows-latest` 上原地构建，构建完成后自动上传到 [VirusTotal](https://www.virustotal.com/) 扫描。**SHA256 与 VirusTotal 分析链接**记录在每个 Release 的发布说明里，可自行核对。

源码开放，可自行 `dotnet publish` 复现产物。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)（Windows）。

```powershell
dotnet run                  # 开发运行
./publish.ps1               # 发布单文件到 publish/VibeCopy.exe
```
