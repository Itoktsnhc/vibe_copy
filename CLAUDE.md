# VibeCopy

Windows GUI 工具：相机/读卡器插入后，把多个可移动盘的照片/视频一次性归档到目标目录下的 `yyyy-MM-dd` 文件夹，完成后一键弹出。

## 技术栈

- **.NET 8 + Avalonia 11**（跨平台 UI，实际只用 Windows；单文件 exe）
- 语言 C# 12
- 弹出先用具备读写权限的卷句柄调用 Win32 `DeviceIoControl`：`FSCTL_LOCK_VOLUME` → `FSCTL_DISMOUNT_VOLUME` → `IOCTL_STORAGE_EJECT_MEDIA`。失败后释放句柄，在 STA 线程回退到资源管理器的 `eject` verb；不能跳过失败的锁卷步骤直接强制弹出。
- 复制走 `FileStream` 分块 + `.part` 临时名 + rename
- 布局全靠 XAML `Grid`/`StackPanel`，别再手算像素

## 目录结构

```
VibeCopy.csproj       # SDK 风格工程，Avalonia PackageReference
app.manifest          # PerMonitor DPI
App.axaml(.cs)        # 入口
MainWindow.axaml(.cs) # 全部 UI + 事件
Core.cs               # Config / Shell / Copier / DriveRow
```

UI 和 Core 两块，别再拆。加东西之前先想想是不是真需要（YAGNI）。

## 开发

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)（Windows）。

```powershell
# 运行
dotnet run

# 发布单文件 exe（COM 资源管理器弹出回退不启用裁剪）
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false
# 产物：bin/Release/net8.0-windows/win-x64/publish/VibeCopy.exe
```

配置文件和日志放 exe 同目录：`vibecopy.config.json` / `vibecopy.log`（首次运行自动生成）。

## 设计约定

- **品牌无关**：默认扩展名覆盖 Sony/Canon/Nikon/Fuji/Panasonic RAW + 常见视频；扫描目录默认 `DCIM,PRIVATE,M4ROOT,XDROOT,MISC,AVCHD,CLIP,SSP`，留空则全盘扫。加机型 = 改默认字符串，别加 if-else。
- **日期字段**：`creation`（Windows 文件创建时间）或 `modified`（写入时间）。默认 creation。
- **同名冲突**：按配置 `skip`（存在即跳过，不比大小）/`rename`（追加 `-N`）/`overwrite`；`.part` 完成后 rename，防止半文件。
- **弹出**：手动及自动弹出共用异步流程，逐盘执行，期间禁用重复操作。COM 返回只代表请求已提交，需检查介质不可访问后才报告成功。发布时保留 `BuiltInComInteropSupport` 并关闭裁剪（内置 COM 与裁剪不兼容）；接口使用明确的 GUID / DISPID，避免动态反射分派。

## 不要做的事

- 不要引 NuGet 包（除非确实必要，Win32 弹出直接 P/Invoke `DeviceIoControl`）
- 不要拆多项目/多文件
- 不要加"进度动画/托盘图标/更新检查"这类非核心功能
- 不要加 EXIF 解析：文件系统时间已经够用；真要 EXIF 再说
