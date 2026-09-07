using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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
        NudConcurrency.ItemsSource = new[] { 1, 2, 3, 4, 6, 8, 12, 16 };
        NudConcurrency.SelectedItem = ((int[])NudConcurrency.ItemsSource).Contains(cfg.Concurrency) ? cfg.Concurrency : 2;
        DgDrives.ItemsSource = drives;
        Opened += (_, _) => RefreshDrives();

        TbTarget.TextChanged += (_, _) => SaveCfg();
        TbExts.TextChanged += (_, _) => SaveCfg();
        TbDirs.TextChanged += (_, _) => SaveCfg();
        CbTime.SelectionChanged += (_, _) => SaveCfg();
        CbConflict.SelectionChanged += (_, _) => SaveCfg();
        CbVerify.IsCheckedChanged += (_, _) => SaveCfg();
        CbAutoEject.IsCheckedChanged += (_, _) => SaveCfg();
        NudConcurrency.SelectionChanged += (_, _) => SaveCfg();
    }

    void SaveCfg()
    {
        cfg.Target = TbTarget.Text ?? "";
        cfg.Exts = TbExts.Text ?? "";
        cfg.ScanDirs = TbDirs.Text ?? "";
        cfg.TimeField = (string?)CbTime.SelectedItem ?? cfg.TimeField;
        cfg.Conflict = (string?)CbConflict.SelectedItem ?? cfg.Conflict;
        cfg.Verify = CbVerify.IsChecked == true;
        cfg.AutoEject = CbAutoEject.IsChecked == true;
        cfg.Concurrency = NudConcurrency.SelectedItem is int c ? c : 2;
        try { cfg.Save(); } catch { }
    }

    async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var top = GetTopLevel(this);
        if (top is null) return;
        var res = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择目标目录",
            AllowMultiple = false,
        });
        if (res.Count > 0)
        {
            var p = res[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) TbTarget.Text = p;
        }
    }

    void BtnRefresh_Click(object? sender, RoutedEventArgs e) => RefreshDrives();

    void BtnEject_Click(object? sender, RoutedEventArgs e)
    {
        BtnEject.IsEnabled = false;
        try
        {
            foreach (var d in drives.Where(x => x.Checked).Select(x => x.Name).ToList())
            {
                var (ok, msg) = Shell.Eject(d);
                Log($"弹出 {d}: {(ok ? "OK" : msg)}");
            }
            RefreshDrives();
        }
        finally { BtnEject.IsEnabled = true; }
    }

    void BtnCancel_Click(object? sender, RoutedEventArgs e) => cts?.Cancel();

    void RefreshDrives()
    {
        drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
        {
            string label = "", fs = ""; long total = 0, free = 0;
            try { if (d.IsReady) { label = d.VolumeLabel; fs = d.DriveFormat; total = d.TotalSize; free = d.AvailableFreeSpace; } }
            catch { }
            drives.Add(new DriveRow
            {
                Checked = true, Name = d.Name, Label = label,
                Total = Copier.Sz(total), Free = Copier.Sz(free), Fs = fs
            });
        }
    }

    async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        var picked = drives.Where(x => x.Checked).Select(x => x.Name).ToList();
        if (picked.Count == 0) { await MessageAsync("请勾选至少一个盘"); return; }
        var target = (TbTarget.Text ?? "").Trim();
        if (string.IsNullOrEmpty(target)) { await MessageAsync("请设置目标目录"); return; }
        Directory.CreateDirectory(target);

        SaveCfg();

        var exts = (TbExts.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .ToHashSet();
        var sdirs = (TbDirs.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        bool useCreation = cfg.TimeField == "creation";

        BtnStart.IsEnabled = false; BtnCancel.IsEnabled = true;
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            Log($"扫描 {picked.Count} 个盘…");
            if (sdirs.Length > 0)
            {
                foreach (var d in picked)
                {
                    var missing = sdirs.Where(s => !Directory.Exists(Path.Combine(d, s))).ToList();
                    if (missing.Count == sdirs.Length)
                        Log($"提示 {d} 未找到任何配置的扫描子目录（{string.Join(",", sdirs)}），此盘将跳过。留空可全盘扫。");
                }
            }
            var files = await Task.Run(() =>
                picked.SelectMany(d => Copier.Scan(d, exts, sdirs))
                      .OrderBy(f => useCreation ? f.Created : f.Modified)
                      .ToList(), ct);
            long totalBytes = files.Sum(f => f.Size);
            int totalFiles = files.Count;
            Log($"待复制 {totalFiles} 个文件，共 {Copier.Sz(totalBytes)}，并发 {cfg.Concurrency}");
            SetOverall(0, totalFiles, 0, totalBytes);
            SetCurrent(0, 1, "—");
            TbSpeed.Text = "0 B/s";

            long doneBytes = 0;
            int doneFiles = 0, copied = 0, skipped = 0, failed = 0;
            var toVerify = new System.Collections.Concurrent.ConcurrentBag<(string src, string dst, long size)>();
            var active = new System.Collections.Concurrent.ConcurrentDictionary<int, (string name, long size, long done)>();
            var sw = Stopwatch.StartNew();
            long lastBytes = 0; double lastSec = 0;

            using var speedTimer = new System.Threading.Timer(_ =>
            {
                var cur = Interlocked.Read(ref doneBytes);
                var nowSec = sw.Elapsed.TotalSeconds;
                var dt = nowSec - lastSec;
                if (dt <= 0) return;
                var speed = (cur - lastBytes) / dt;
                lastBytes = cur; lastSec = nowSec;
                var snapshot = active.Values.ToArray();
                Dispatcher.UIThread.Post(() =>
                {
                    TbSpeed.Text = Copier.Sz(speed) + "/s";
                    if (snapshot.Length > 0)
                    {
                        long cd = snapshot.Sum(x => x.done);
                        long cs = Math.Max(1, snapshot.Sum(x => x.size));
                        var name = snapshot.Length == 1 ? snapshot[0].name : $"{snapshot.Length} 个文件并行";
                        SetCurrent(cd, cs, name);
                    }
                });
            }, null, 250, 250);

            await Parallel.ForEachAsync(files,
                new ParallelOptions { MaxDegreeOfParallelism = cfg.Concurrency, CancellationToken = ct },
                (f, token) =>
                {
                    if (token.IsCancellationRequested) return ValueTask.CompletedTask;
                    var ts = useCreation ? f.Created : f.Modified;
                    var folder = Path.Combine(target, ts.ToString("yyyy-MM-dd"));
                    var dst = Path.Combine(folder, Path.GetFileName(f.Src));
                    int slot = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    try
                    {
                        string action = "复制";
                        if (File.Exists(dst))
                        {
                            if (cfg.Conflict == "skip")
                            {
                                Interlocked.Increment(ref skipped);
                                Interlocked.Add(ref doneBytes, f.Size);
                                var df = Interlocked.Increment(ref doneFiles);
                                Log($"跳过 {f.Src} → {dst}（已存在）");
                                Dispatcher.UIThread.Post(() => SetOverall(df, totalFiles, Interlocked.Read(ref doneBytes), totalBytes));
                                return ValueTask.CompletedTask;
                            }
                            if (cfg.Conflict == "rename") { dst = Copier.UniquePath(dst); action = "改名"; }
                            else action = "覆盖";
                        }
                        Log($"{action} {f.Src} → {dst}  ({Copier.Sz(f.Size)})");
                        var srcName = Path.GetFileName(f.Src);
                        active[slot] = (srcName, f.Size, 0);
                        bool ok = Copier.CopyOne(f.Src, dst, n =>
                        {
                            Interlocked.Add(ref doneBytes, n);
                            active.AddOrUpdate(slot,
                                _ => (srcName, f.Size, n),
                                (_, cur) => (cur.name, cur.size, cur.done + n));
                        }, token);
                        active.TryRemove(slot, out _);
                        if (ok)
                        {
                            Interlocked.Increment(ref copied);
                            if (cfg.Verify) toVerify.Add((f.Src, dst, f.Size));
                        }
                        else if (token.IsCancellationRequested) Log($"已取消 {f.Src}");
                    }
                    catch (Exception ex) { Interlocked.Increment(ref failed); Log($"失败 {f.Src}: {ex.Message}"); active.TryRemove(slot, out _); }
                    var df2 = Interlocked.Increment(ref doneFiles);
                    Dispatcher.UIThread.Post(() => SetOverall(df2, totalFiles, Interlocked.Read(ref doneBytes), totalBytes));
                    return ValueTask.CompletedTask;
                });

            speedTimer.Dispose();
            sw.Stop();
            var avg = doneBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.01);
            Dispatcher.UIThread.Post(() => { TbSpeed.Text = Copier.Sz(avg) + "/s"; SetCurrent(1, 1, "—"); });
            Log($"复制完成：复制 {copied}，跳过 {skipped}，失败 {failed}，{Copier.Sz(doneBytes)} in {sw.Elapsed.TotalSeconds:0.0}s（{Copier.Sz(avg)}/s）");

            if (cfg.Verify && toVerify.Count > 0 && !ct.IsCancellationRequested)
            {
                var vlist = toVerify.ToList();
                int vtotalFiles = vlist.Count;
                Log($"开始校验 {vtotalFiles} 个文件");
                await Dispatcher.UIThread.InvokeAsync(() => { SetOverall(0, vtotalFiles, 0, 1); SetCurrent(0, 1, "校验中"); });
                int vdoneFiles = 0, vok = 0, vbad = 0;
                var vsw = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    foreach (var (src, dst, size) in vlist)
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
                        var df = Interlocked.Increment(ref vdoneFiles);
                        var name = Path.GetFileName(dst);
                        Dispatcher.UIThread.Post(() => { SetOverall(df, vtotalFiles, df, vtotalFiles); SetCurrent(1, 1, name); });
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

    void SetOverall(int doneFiles, int totalFiles, long doneBytes, long totalBytes)
    {
        PbOverall.Maximum = Math.Max(1, totalFiles);
        PbOverall.Value = Math.Min(doneFiles, totalFiles);
        var pct = 100.0 * doneFiles / Math.Max(1, totalFiles);
        LbOverall.Text = $"总进度  {doneFiles} / {totalFiles}  ({pct:0.0}%)  {Copier.Sz(doneBytes)} / {Copier.Sz(totalBytes)}";
        LbStatus.Text = doneFiles >= totalFiles ? "完成" : $"进行中  {doneFiles}/{totalFiles}";
    }

    void SetCurrent(long done, long size, string name)
    {
        PbCurrent.Maximum = Math.Max(1, size);
        PbCurrent.Value = Math.Min(done, size);
        var pct = 100.0 * done / Math.Max(1, size);
        LbCurrent.Text = $"当前  {pct:0.0}%   {name}";
    }

    readonly object _logLock = new();
    void Log(string s)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}";
        lock (_logLock)
        {
            try { Directory.CreateDirectory(Config.LogDir); File.AppendAllText(Config.LogPath, line + Environment.NewLine); } catch { }
        }
        Dispatcher.UIThread.Post(() =>
        {
            const int cap = 200_000;
            var cur = TbLog.Text ?? "";
            var next = cur + line + Environment.NewLine;
            if (next.Length > cap) next = next[^cap..];
            TbLog.Text = next;
            TbLog.CaretIndex = TbLog.Text?.Length ?? 0;
        });
    }

    async Task MessageAsync(string s)
    {
        var w = new Window
        {
            Title = "提示", Width = 320, Height = 130,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false, ShowInTaskbar = false,
        };
        var ok = new Button { Content = "确定", Width = 72, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = s, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                ok,
            }
        };
        await w.ShowDialog(this);
    }
}
