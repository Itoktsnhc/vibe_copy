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
    public static string LogPath => Path.Combine(AppContext.BaseDirectory, "vibecopy.log");

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(Path_)) ?? new(); }
        catch { return new(); }
    }
    public void Save() => File.WriteAllText(Path_,
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class Shell
{
    public static (bool ok, string msg) Eject(string driveLetter)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application")
                    ?? throw new InvalidOperationException("Shell.Application unavailable");
            dynamic shell = Activator.CreateInstance(t)!;
            var ns = shell.Namespace(17);
            var item = ns?.ParseName(driveLetter.TrimEnd('\\'));
            if (item == null) return (false, "not found");
            item.InvokeVerb("Eject");
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
        if (!roots.Any()) roots = new[] { drive };

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
            File.SetCreationTime(tmp, srcFi.CreationTime);
            File.SetLastWriteTime(tmp, srcFi.LastWriteTime);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
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
