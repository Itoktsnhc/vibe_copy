# VibeCopy

Windows GUI 工具：相机/读卡器插入后，把多个可移动盘的照片/视频一次性归档到目标目录下的 `yyyy-MM-dd` 文件夹，完成后一键弹出。

## 技术栈

- **.NET 8 + WPF**（原生 Windows GUI，无第三方依赖）
- 语言 C# 12
- 弹出走 `Shell.Application` COM 的 `Eject` verb（对 U 盘/SD 卡/读卡器足够）
- 复制走 `FileStream` 分块 + `.part` 临时名 + rename
- 布局全靠 XAML `Grid`/`StackPanel`，别再手算像素

## 目录结构

```
VibeCopy.csproj      # SDK 风格工程，UseWPF=true
app.manifest         # PerMonitor DPI
App.xaml(.cs)        # 入口
MainWindow.xaml(.cs) # 全部 UI + 事件
Core.cs              # Config / Shell / Copier / DriveRow
```

UI 和 Core 两块，别再拆。加东西之前先想想是不是真需要（YAGNI）。

## 开发

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)（Windows）。

```powershell
# 运行
dotnet run

# 发布单文件 exe
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# 产物：bin/Release/net8.0-windows/win-x64/publish/VibeCopy.exe
```

配置文件和日志放 exe 同目录：`vibecopy.config.json` / `vibecopy.log`（首次运行自动生成）。

## 设计约定

- **品牌无关**：默认扩展名覆盖 Sony/Canon/Nikon/Fuji/Panasonic RAW + 常见视频；扫描目录默认 `DCIM,PRIVATE,M4ROOT,XDROOT,MISC,AVCHD,CLIP,SSP`，留空则全盘扫。加机型 = 改默认字符串，别加 if-else。
- **日期字段**：`creation`（Windows 文件创建时间）或 `modified`（写入时间）。默认 creation。
- **同名同大小跳过**，其余覆盖；`.part` 完成后 rename，防止半文件。
- **弹出**：勾选盘后按钮触发，每个盘调一次 Shell verb。多盘同物理设备时 Windows 自己会合并处理。

## 不要做的事

- 不要引 NuGet 包（除非确实必要，Shell COM 用 `Type.GetTypeFromProgID` + dynamic 就够）
- 不要拆多项目/多文件
- 不要加"进度动画/托盘图标/更新检查"这类非核心功能
- 不要加 EXIF 解析：文件系统时间已经够用；真要 EXIF 再说
