using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace VibeCopy;

public partial class MainWindow : Window
{
    readonly Config cfg = Config.Load();
    readonly ObservableCollection<DriveRow> drives = new();
    CancellationTokenSource? cts;

    public MainWindow()
    {
        InitializeComponent();
        TbTarget.Text = cfg.Target;
        TbExts.Text = cfg.Exts;
        TbDirs.Text = cfg.ScanDirs;
        CbTime.ItemsSource = new[] { "creation", "modified" };
        CbConflict.ItemsSource = new[] { "skip", "rename", "overwrite" };
        CbTime.SelectedItem = cfg.TimeField;
        CbConflict.SelectedItem = cfg.Conflict;
        CbVerify.IsChecked = cfg.Verify;
        CbAutoEject.IsChecked = cfg.AutoEject;
        DgDrives.ItemsSource = drives;
        Loaded += (_, _) => RefreshDrives();
    }

    void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { InitialDirectory = TbTarget.Text };
        if (d.ShowDialog() == true) TbTarget.Text = d.FolderName;
    }

    void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    void BtnEject_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in drives.Where(x => x.Checked).Select(x => x.Name).ToList())
        {
            var (ok, msg) = Shell.Eject(d);
            Log($"弹出 {d}: {(ok ? "OK" : msg)}");
        }
        RefreshDrives();
    }

    void BtnCancel_Click(object sender, RoutedEventArgs e) => cts?.Cancel();

    void RefreshDrives()
    {
        drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
        {
            string label = "", fs = ""; long total = 0, free = 0;
            try { if (d.IsReady) { label = d.VolumeLabel; fs = d.DriveFormat; total = d.TotalSize; free = d.AvailableFreeSpace; } }
            catch { }
            drives.Add(new DriveRow {
                Checked = true, Name = d.Name, Label = label,
                Total = Copier.Sz(total), Free = Copier.Sz(free), Fs = fs
            });
        }
    }

    async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var picked = drives.Where(x => x.Checked).Select(x => x.Name).ToList();
        if (picked.Count == 0) { MessageBox.Show("请勾选至少一个盘"); return; }
        var target = TbTarget.Text.Trim();
        if (string.IsNullOrEmpty(target)) { MessageBox.Show("请设置目标目录"); return; }
        Directory.CreateDirectory(target);

        cfg.Target = target; cfg.Exts = TbExts.Text; cfg.ScanDirs = TbDirs.Text;
        cfg.TimeField = (string)CbTime.SelectedItem!;
        cfg.Conflict = (string)CbConflict.SelectedItem!;
        cfg.Verify = CbVerify.IsChecked == true;
        cfg.AutoEject = CbAutoEject.IsChecked == true;
        cfg.Save();

        var exts = TbExts.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .ToHashSet();
        var sdirs = TbDirs.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        bool useCreation = cfg.TimeField == "creation";

        BtnStart.IsEnabled = false; BtnCancel.IsEnabled = true;
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            Log($"扫描 {picked.Count} 个盘…");
            var files = await Task.Run(() =>
                picked.SelectMany(d => Copier.Scan(d, exts, sdirs))
                      .OrderBy(f => useCreation ? f.Created : f.Modified)
                      .ToList(), ct);
            long total = files.Sum(f => f.Size);
            Log($"待复制 {files.Count} 个文件，共 {Copier.Sz(total)}");
            Pb.Maximum = Math.Max(1, total); Pb.Value = 0;
            LbProgress.Text = $"0.0%  0 / {Copier.Sz(total)}";

            long done = 0; int copied = 0, skipped = 0, failed = 0;
            var toVerify = new List<(string src, string dst, long size)>();
            var sw = Stopwatch.StartNew();
            long lastBytes = 0; var lastTick = sw.Elapsed;

            await Task.Run(() =>
            {
                foreach (var f in files)
                {
                    if (ct.IsCancellationRequested) break;
                    var ts = useCreation ? f.Created : f.Modified;
                    var folder = Path.Combine(target, ts.ToString("yyyy-MM-dd"));
                    var dst = Path.Combine(folder, Path.GetFileName(f.Src));
                    try
                    {
                        string action = "复制";
                        if (File.Exists(dst))
                        {
                            if (cfg.Conflict == "skip")
                            {
                                skipped++; Interlocked.Add(ref done, f.Size);
                                Log($"跳过 {f.Src} → {dst}（已存在）");
                                Report(done, total, null, null); continue;
                            }
                            if (cfg.Conflict == "rename") { dst = Copier.UniquePath(dst); action = "改名"; }
                            else action = "覆盖";
                        }
                        Log($"{action} {f.Src} → {dst}  ({Copier.Sz(f.Size)})");

                        var srcName = Path.GetFileName(f.Src);
                        bool ok = Copier.CopyOne(f.Src, dst, n =>
                        {
                            Interlocked.Add(ref done, n);
                            var now = sw.Elapsed;
                            if ((now - lastTick).TotalMilliseconds >= 200)
                            {
                                var speed = (done - lastBytes) / (now - lastTick).TotalSeconds;
                                lastTick = now; lastBytes = done;
                                Report(done, total, speed, srcName);
                            }
                        }, ct);
                        if (ok)
                        {
                            copied++;
                            if (cfg.Verify) toVerify.Add((f.Src, dst, f.Size));
                        }
                    }
                    catch (Exception ex) { failed++; Log($"失败 {f.Src}: {ex.Message}"); }
                    Report(done, total, null, null);
                }
            }, ct);

            sw.Stop();
            var avg = done / Math.Max(sw.Elapsed.TotalSeconds, 0.01);
            Log($"复制完成：复制 {copied}，跳过 {skipped}，失败 {failed}，{Copier.Sz(done)} in {sw.Elapsed.TotalSeconds:0.0}s（{Copier.Sz(avg)}/s）");

            if (cfg.Verify && toVerify.Count > 0 && !ct.IsCancellationRequested)
            {
                long vtotal = toVerify.Sum(x => x.size);
                Log($"开始校验 {toVerify.Count} 个文件，共 {Copier.Sz(vtotal)}");
                Dispatcher.Invoke(() => { Pb.Maximum = Math.Max(1, vtotal); Pb.Value = 0; });
                long vdone = 0; int vok = 0, vbad = 0;
                var vsw = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    foreach (var (src, dst, size) in toVerify)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            var a = Copier.Sha1(src);
                            var b = Copier.Sha1(dst);
                            if (a == b) { vok++; Log($"校验通过 {dst}  src={a}  dst={b}"); }
                            else { vbad++; failed++; Log($"校验失败 {dst}  src={a}  dst={b}"); try { File.Delete(dst); } catch { } }
                        }
                        catch (Exception ex) { vbad++; failed++; Log($"校验错误 {dst}: {ex.Message}"); }
                        Interlocked.Add(ref vdone, size);
                        Report(vdone, vtotal, null, Path.GetFileName(dst));
                    }
                }, ct);
                vsw.Stop();
                Log($"校验完成：通过 {vok}，失败 {vbad}，{vsw.Elapsed.TotalSeconds:0.0}s");
            }
            if (cfg.AutoEject && failed == 0 && !ct.IsCancellationRequested)
            {
                foreach (var d in picked)
                {
                    var (ok, msg) = Shell.Eject(d);
                    Log($"弹出 {d}: {(ok ? "OK" : msg)}");
                }
            }
        }
        catch (OperationCanceledException) { Log("已取消"); }
        catch (Exception ex) { Log("错误：" + ex.Message); }
        finally
        {
            BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false;
            cts?.Dispose(); cts = null;
            RefreshDrives();
        }
    }

    void Report(long done, long total, double? speed, string? name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Pb.Value = done;
            var pct = 100.0 * done / Math.Max(1, total);
            var over = speed is null
                ? $"{pct:0.0}%  {Copier.Sz(done)} / {Copier.Sz(total)}"
                : $"{pct:0.0}%  {Copier.Sz(done)} / {Copier.Sz(total)}   •   {Copier.Sz(speed.Value)}/s";
            LbProgress.Text = over;
            if (name is not null) LbStatus.Text = "正在复制  " + name;
        });
    }

    void Log(string s)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}";
        try { File.AppendAllText(Config.LogPath, line + Environment.NewLine); } catch { }
        Dispatcher.BeginInvoke(() =>
        {
            TbLog.AppendText(line + Environment.NewLine);
            TbLog.ScrollToEnd();
        });
    }
}
