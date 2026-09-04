using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace VibeCopy;

public class Config
{
    public string Target { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VibeCopy");
    public string Exts { get; set; } = ".arw,.cr2,.cr3,.nef,.raf,.rw2,.dng,.jpg,.jpeg,.heic,.heif,.mp4,.mov,.mts,.m2ts,.avi,.insv,.xml,.thm";
    public string ScanDirs { get; set; } = "DCIM,PRIVATE,M4ROOT,XDROOT,MISC,AVCHD,CLIP,SSP";
    public string TimeField { get; set; } = "creation"; // creation | modified
    public string Conflict { get; set; } = "rename";    // skip | rename | overwrite
    public bool Verify { get; set; } = false;
    public bool AutoEject { get; set; } = true;

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

    public static (bool ok, string msg) Eject(string driveLetter)
    {
        var letter = driveLetter.TrimEnd('\\', '/').TrimEnd(':');
        if (letter.Length == 0) return (false, "empty");
        var path = $@"\\.\{letter}:";
        try
        {
            using var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return (false, $"open failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            if (!DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return (false, $"lock failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return (false, $"dismount failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            var pmr = new PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = 0 };
            DeviceIoControl(h, IOCTL_STORAGE_MEDIA_REMOVAL, ref pmr, (uint)System.Runtime.InteropServices.Marshal.SizeOf(pmr), IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return (false, $"eject failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return (true, "ok");
        }
        catch (Exception e) { return (false, e.Message); }
    }
}

public record MediaFile(string Drive, string Src, long Size, DateTime Created, DateTime Modified);

public static class Copier
{
    public static IEnumerable<MediaFile> Scan(string drive, HashSet<string> exts, string[] scanDirs)
    {
        IEnumerable<string> roots = scanDirs.Length == 0
            ? new[] { drive }
            : scanDirs.Select(d => Path.Combine(drive, d)).Where(Directory.Exists);

        foreach (var r in roots)
        {
            IEnumerator<string> it;
            try { it = Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).GetEnumerator(); }
            catch { continue; }
            while (true)
            {
                string p;
                try { if (!it.MoveNext()) break; p = it.Current; }
                catch { continue; }
                if (!exts.Contains(Path.GetExtension(p).ToLowerInvariant())) continue;
                FileInfo fi;
                try { fi = new FileInfo(p); } catch { continue; }
                yield return new MediaFile(drive, p, fi.Length, fi.CreationTime, fi.LastWriteTime);
            }
        }
    }

    public static bool CopyOne(string src, string dst, Action<int> onBytes,
                               CancellationToken ct, int chunk = 4 * 1024 * 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + ".part";
        try
        {
            using (var r = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.SequentialScan))
            using (var w = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, chunk, FileOptions.SequentialScan))
            {
                var buf = new byte[chunk];
                int n;
                while ((n = r.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) { w.Dispose(); File.Delete(tmp); return false; }
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
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                    break;
                }
                catch (IOException) when (i < 4) { Thread.Sleep(200 * (i + 1)); }
            }
            return true;
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    public static string UniquePath(string p)
    {
        var dir = Path.GetDirectoryName(p)!;
        var name = Path.GetFileNameWithoutExtension(p);
        var ext = Path.GetExtension(p);
        for (int i = 1; ; i++)
        {
            var cand = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(cand)) return cand;
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
