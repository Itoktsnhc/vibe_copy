using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibeCopy;

public class Config
{
    public string Target { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VibeCopy");
    // One source directory per line. An empty value keeps the removable-drive shortcut behavior.
    public string SourceDirs { get; set; } = "";
    // Empty filters include all file types and all subdirectories.
    public string Exts { get; set; } = "";
    public string ScanDirs { get; set; } = "";
    public string FolderPattern { get; set; } = "yyyy-MM-dd";
    public string TimeField { get; set; } = "creation"; // creation | modified
    public string Conflict { get; set; } = "rename";    // skip | rename | overwrite
    public bool Verify { get; set; } = false;
    public bool AutoEject { get; set; } = true;
    public int Concurrency { get; set; } = 2;

    static string Path_ => Path.Combine(AppContext.BaseDirectory, "vibecopy.config.json");
    public static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");
    public static string LogPath => Path.Combine(LogDir, $"vibecopy-{DateTime.Now:yyyy-MM-dd}.log");

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(Path_), CfgCtx.Default.Config) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        var tmp = Path_ + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, CfgCtx.Default.Config));
        if (File.Exists(Path_)) File.Replace(tmp, Path_, null);
        else File.Move(tmp, Path_);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Config))]
internal partial class CfgCtx : System.Text.Json.Serialization.JsonSerializerContext { }

public static class Shell
{
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    const uint OPEN_EXISTING = 3;
    const uint FSCTL_LOCK_VOLUME = 0x00090018;
    const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;
    const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;

    // Explicit dispatch interfaces avoid runtime member lookup for the Explorer fallback.
    // GUIDs and DISPIDs come from the Windows Shell type library (ShlDisp.idl).
    [ComImport, Guid("13709620-C279-11CE-A49E-444553540000")]
    class ShellApplication { }

    [ComImport, Guid("D8F015C0-C278-11CE-A49E-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IShellDispatch
    {
        [DispId(0x60020002)]
        [return: MarshalAs(UnmanagedType.Interface)]
        IShellFolder? NameSpace([MarshalAs(UnmanagedType.Struct)] object directory);
    }

    [ComImport, Guid("BBCBDE60-C3FF-11CE-8350-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IShellFolder
    {
        [DispId(0x60020005)]
        [return: MarshalAs(UnmanagedType.Interface)]
        IShellItem? ParseName([MarshalAs(UnmanagedType.BStr)] string name);
    }

    [ComImport, Guid("FAC32C80-CBE4-11CE-8350-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IShellItem
    {
        [DispId(0x60020010)]
        void InvokeVerb([MarshalAs(UnmanagedType.Struct)] object verb);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PREVENT_MEDIA_REMOVAL { public byte PreventMediaRemoval; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint attr, IntPtr tmpl);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code,
        IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint bytesReturned, IntPtr overlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code,
        ref PREVENT_MEDIA_REMOVAL inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint bytesReturned, IntPtr overlapped);

    public static Task<(bool ok, string msg)> EjectAsync(string driveLetter)
    {
        var completion = new TaskCompletionSource<(bool ok, string msg)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Shell automation needs an STA; device calls and completion checks must not block the UI.
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Eject(driveLetter)); }
            catch (Exception ex) { completion.SetResult((false, $"弹出失败：{ex.Message}")); }
        }) { IsBackground = true, Name = "VibeCopy Eject" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    static (bool ok, string msg) Eject(string driveLetter)
    {
        var root = driveLetter.Trim().Replace('/', '\\').ToUpperInvariant();
        if (root.Length == 2) root += "\\";
        if (root.Length != 3 || root[0] is < 'A' or > 'Z' || root[1] != ':' || root[2] != '\\')
            return (false, "弹出失败：请选择移动盘的盘符根目录。");

        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (drive is null) return (false, "盘符已不存在，未执行弹出。");
            if (drive.DriveType != DriveType.Removable) return (false, "仅支持弹出可移动盘。");
            if (!drive.IsReady) return (false, "没有可访问的介质，可能已弹出或尚未插卡。");

            var direct = EjectDirect(root);
            if (direct.ok) return direct;
            // EjectDirect disposes its volume handle before Explorer takes over. Never bypass a
            // failed lock by forcing dismount/eject; let Explorer perform its own safe removal.
            var explorer = EjectViaExplorer(root);
            return explorer.ok
                ? (true, $"{explorer.msg}；直接接口未成功：{direct.msg}")
                : (false, $"未确认弹出。直接接口：{direct.msg}；资源管理器：{explorer.msg}");
        }
        catch (Exception ex) { return (false, $"弹出失败：{ex.Message}"); }
    }

    static (bool ok, string msg) EjectDirect(string root)
    {
        try
        {
            // STORAGE_MEDIA_REMOVAL / EJECT_MEDIA require FILE_READ_ACCESS. The volume
            // lock/dismount sequence uses a read/write handle (access=0 is query-only).
            using var h = CreateFileW($@"\\.\{root[..2]}", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return NativeError("打开设备");
            if (!DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return NativeError("锁定卷");
            if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return NativeError("卸载卷");
            var pmr = new PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = 0 };
            if (!DeviceIoControl(h, IOCTL_STORAGE_MEDIA_REMOVAL, ref pmr, (uint)Marshal.SizeOf<PREVENT_MEDIA_REMOVAL>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
                return NativeError("解除介质锁定");
            if (!DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return NativeError("弹出介质");
            return (true, "已弹出（设备接口）");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    static (bool ok, string msg) NativeError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        var hint = code switch
        {
            1 or 50 => "设备或驱动不支持此操作",
            5 => "访问被拒绝，可能权限不足或卷被占用",
            32 or 33 or 170 => "设备被占用，请关闭打开此盘文件的程序和窗口",
            21 or 1112 => "介质未就绪，可能已经弹出",
            _ => new Win32Exception(code).Message,
        };
        return (false, $"{operation}失败（{code}）：{hint}");
    }

    static (bool ok, string msg) EjectViaExplorer(string root)
    {
        IShellDispatch? shell = null;
        IShellFolder? computer = null;
        IShellItem? item = null;
        try
        {
            shell = (IShellDispatch)new ShellApplication();
            computer = shell.NameSpace(17); // ssfDRIVES / This PC
            item = computer?.ParseName(root);
            if (item is null) return (false, "无法找到盘符对应的系统项目");
            // Canonical verb works regardless of the Windows display language.
            item.InvokeVerb("eject");
            // InvokeVerb only submits a request. An occupied drive can remain ready even when
            // COM reports success, so confirm removal before reporting that it is safe to unplug.
            for (int i = 0; i < 20; i++)
            {
                var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (drive is null || !drive.IsReady) return (true, "已弹出（资源管理器）");
                Thread.Sleep(250);
            }
            return (false, "已提交弹出请求，但介质仍可访问；请检查系统提示，关闭占用文件或目录的程序后重试");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (computer is not null) Marshal.FinalReleaseComObject(computer);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}

public record MediaFile(string SourceRoot, string Src, long Size, DateTime Created, DateTime Modified);
public record CopyJob(MediaFile File, string Destination, bool Skip);

public static class Copier
{
    public static IEnumerable<MediaFile> Scan(string source, HashSet<string> exts, string[] scanDirs,
                                               string? excludedRoot = null, CancellationToken ct = default,
                                               Action<string>? onError = null)
    {
        IEnumerable<string> roots = scanDirs.Length == 0
            ? new[] { source }
            : scanDirs.Select(d => Path.Combine(source, d)).Where(Directory.Exists);

        var excluded = excludedRoot is null ? null : Path.GetFullPath(excludedRoot);
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };

        foreach (var r in roots)
        {
            var pending = new Stack<string>();
            pending.Push(r);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                if (excluded is not null && IsSameOrChild(dir, excluded)) continue;
                string[] children, files;
                try
                {
                    children = Directory.GetDirectories(dir, "*", options);
                    files = Directory.GetFiles(dir, "*", options);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    onError?.Invoke($"无法扫描 {dir}: {ex.Message}");
                    continue;
                }
                foreach (var child in children) pending.Push(child);
                foreach (var p in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (exts.Count > 0 && !exts.Contains(Path.GetExtension(p).ToLowerInvariant())) continue;
                    MediaFile file;
                    try
                    {
                        var fi = new FileInfo(p);
                        file = new MediaFile(source, p, fi.Length, fi.CreationTime, fi.LastWriteTime);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        onError?.Invoke($"无法读取 {p}: {ex.Message}");
                        continue;
                    }
                    yield return file;
                }
            }
        }
    }

    public static bool IsSameOrChild(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryValidateFolderPattern(string pattern, out string error)
    {
        try
        {
            _ = FormatFolder(DateTime.Now, pattern);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string FormatFolder(DateTime timestamp, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new FormatException("目标子目录规则不能为空。");

        var relative = timestamp.ToString(pattern.Trim().Replace('\\', '/'), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new FormatException("目标子目录规则必须生成相对路径。");

        var invalid = Path.GetInvalidFileNameChars();
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." ||
                           p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(invalid) >= 0 || IsReservedName(p)))
            throw new FormatException("目标子目录规则生成了无效的目录名。");
        return Path.Combine(parts);
    }

    static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
               (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                stem[3] is >= '1' and <= '9');
    }

    public static IEnumerable<CopyJob> PlanCopies(IEnumerable<MediaFile> files, string target, string pattern,
                                                  bool useCreation, string conflict, CancellationToken ct = default)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var folder = FormatFolder(useCreation ? file.Created : file.Modified, pattern);
            var dst = Path.GetFullPath(Path.Combine(target, folder, Path.GetFileName(file.Src)));
            bool Exists(string path) => reserved.Contains(path) || File.Exists(path) || Directory.Exists(path);
            bool skip = false;
            if (Exists(dst))
            {
                if (conflict == "skip") skip = true;
                else if (conflict == "rename") dst = UniquePath(dst, Exists);
            }
            reserved.Add(dst);
            yield return new CopyJob(file, dst, skip);
        }
    }

    public static bool CopyOne(string src, string dst, Action<int> onBytes,
                               CancellationToken ct, bool overwrite = false, int chunk = 4 * 1024 * 1024)
    {
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            throw new IOException("来源文件与目标文件不能相同。");
        if (ct.IsCancellationRequested) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = Path.Combine(Path.GetDirectoryName(dst)!, $".vibecopy-{Guid.NewGuid():N}.part");
        try
        {
            using (var r = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.SequentialScan))
            using (var w = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunk, FileOptions.SequentialScan))
            {
                var buf = new byte[chunk];
                int n;
                while ((n = r.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    w.Write(buf, 0, n);
                    onBytes(n);
                }
            }
            var srcFi = new FileInfo(src);
            try { File.SetCreationTime(tmp, srcFi.CreationTime); } catch { }
            try { File.SetLastWriteTime(tmp, srcFi.LastWriteTime); } catch { }
            // ponytail: retry the final rename a few times — SMB/NAS with AV briefly locks .part
            for (int i = 0; ; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Move(tmp, dst, overwrite);
                    break;
                }
                catch (IOException) when (i < 4 && (overwrite || !File.Exists(dst)))
                {
                    if (ct.WaitHandle.WaitOne(200 * (i + 1))) ct.ThrowIfCancellationRequested();
                }
            }
            return true;
        }
        catch (OperationCanceledException) { try { File.Delete(tmp); } catch { } return false; }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    public static string UniquePath(string p, Func<string, bool>? exists = null)
    {
        var dir = Path.GetDirectoryName(p)!;
        var name = Path.GetFileNameWithoutExtension(p);
        var ext = Path.GetExtension(p);
        for (int i = 1; ; i++)
        {
            var cand = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!(exists?.Invoke(cand) ?? (File.Exists(cand) || Directory.Exists(cand)))) return cand;
        }
    }

    public static string Sz(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0; while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return $"{n:0.#}{u[i]}";
    }

    public static string Sha1(string path)
    {
        using var s = File.OpenRead(path);
        using var h = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(h.ComputeHash(s));
    }
}

public class DriveRow
{
    public bool Checked { get; set; } = true;
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Total { get; set; } = "";
    public string Free { get; set; } = "";
    public string Fs { get; set; } = "";
}
