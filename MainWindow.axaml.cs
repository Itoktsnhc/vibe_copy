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
    bool ejecting;

    public MainWindow()
    {
        InitializeComponent();
        TbTarget.Text = cfg.Target;
        TbSources.Text = cfg.SourceDirs;
        TbExts.Text = cfg.Exts;
        TbDirs.Text = cfg.ScanDirs;
        TbFolderPattern.Text = cfg.FolderPattern;
        CbFolderPreset.ItemsSource = new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy/MM", "yyyy" };
        UpdateFolderPreview();
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
        TbSources.TextChanged += (_, _) => SaveCfg();
        TbExts.TextChanged += (_, _) => SaveCfg();
        TbDirs.TextChanged += (_, _) => SaveCfg();
        TbFolderPattern.TextChanged += (_, _) => { UpdateFolderPreview(); SaveCfg(); };
        CbFolderPreset.SelectionChanged += (_, _) =>
        {
            if (CbFolderPreset.SelectedItem is string pattern) TbFolderPattern.Text = pattern;
        };
        CbTime.SelectionChanged += (_, _) => SaveCfg();
        CbConflict.SelectionChanged += (_, _) => SaveCfg();
        CbVerify.IsCheckedChanged += (_, _) => SaveCfg();
        CbAutoEject.IsCheckedChanged += (_, _) => SaveCfg();
        NudConcurrency.SelectionChanged += (_, _) => SaveCfg();
    }

    void SaveCfg()
    {
        cfg.Target = TbTarget.Text ?? "";
        cfg.SourceDirs = TbSources.Text ?? "";
        cfg.Exts = TbExts.Text ?? "";
        cfg.ScanDirs = TbDirs.Text ?? "";
        cfg.FolderPattern = TbFolderPattern.Text ?? "";
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

    void UpdateFolderPreview()
    {
        var pattern = TbFolderPattern.Text ?? "";
        LbFolderPreview.Text = Copier.TryValidateFolderPattern(pattern, out var error)
            ? $"目录示例：{Copier.FormatFolder(DateTime.Today, pattern)}（按每个文件的时间整理）"
            : $"规则无效：{error}";
    }

    async void BtnBrowseSources_Click(object? sender, RoutedEventArgs e)
    {
        var top = GetTopLevel(this);
        if (top is null) return;
        var res = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择来源目录",
            AllowMultiple = true,
        });
        var paths = res.Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;
        var current = (TbSources.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        TbSources.Text = string.Join(Environment.NewLine, current.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    void BtnRefresh_Click(object? sender, RoutedEventArgs e) => RefreshDrives();

    async void BtnEject_Click(object? sender, RoutedEventArgs e)
    {
        if (ejecting || cts is not null) return;
        var picked = drives.Where(x => x.Checked).Select(x => x.Name).ToList();
        if (picked.Count == 0) { await MessageAsync("请先勾选需要弹出的移动盘。"); return; }
        ejecting = true;
        BtnStart.IsEnabled = false;
        SettingsPanel.IsEnabled = false; DrivesPanel.IsEnabled = false;
        LbStatus.Text = "正在请求 Windows 安全弹出…";
        try
        {
            var (succeeded, failed) = await EjectDrivesAsync(picked);
            LbStatus.Text = $"弹出完成：成功 {succeeded}，未确认 {failed}，详情见日志";
        }
        catch (Exception ex) { Log($"弹出失败：{ex.Message}"); LbStatus.Text = "弹出失败，详情见日志"; }
        finally
        {
            ejecting = false;
            BtnStart.IsEnabled = true;
            SettingsPanel.IsEnabled = true; DrivesPanel.IsEnabled = true;
            RefreshDrives();
        }
    }

    async Task<(int succeeded, int failed)> EjectDrivesAsync(IEnumerable<string> picked)
    {
        int succeeded = 0, failed = 0;
        foreach (var d in picked.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (ok, msg) = await Shell.EjectAsync(d);
            Log($"弹出 {d}: {msg}");
            if (ok)
            {
                succeeded++;
                foreach (var row in drives.Where(x => x.Name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                    row.Checked = false;
            }
            else failed++;
        }
        return (succeeded, failed);
    }

    void BtnCancel_Click(object? sender, RoutedEventArgs e) => cts?.Cancel();

    void RefreshDrives()
    {
        var previous = drives.ToDictionary(d => d.Name, d => d.Checked, StringComparer.OrdinalIgnoreCase);
        drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
        {
            string label = "", fs = ""; long total = 0, free = 0;
            try { if (d.IsReady) { label = d.VolumeLabel; fs = d.DriveFormat; total = d.TotalSize; free = d.AvailableFreeSpace; } }
            catch { }
            drives.Add(new DriveRow
            {
                Checked = previous.TryGetValue(d.Name, out var wasChecked) && wasChecked,
                Name = d.Name, Label = label,
                Total = Copier.Sz(total), Free = Copier.Sz(free), Fs = fs
            });
        }
    }

    async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        if (cts is not null || ejecting) return;
        var picked = drives.Where(x => x.Checked).Select(x => x.Name).ToList();
        var configuredSources = (TbSources.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sources = configuredSources.Concat(picked).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) { await MessageAsync("请配置或选择至少一个来源目录"); return; }
        var target = (TbTarget.Text ?? "").Trim();
        if (string.IsNullOrEmpty(target)) { await MessageAsync("请设置目标目录"); return; }
        var pattern = (TbFolderPattern.Text ?? "").Trim();
        if (!Copier.TryValidateFolderPattern(pattern, out var patternError))
        {
            await MessageAsync($"目标子目录规则无效：{patternError}\n示例：yyyy/MM/dd");
            return;
        }
        SaveCfg();

        var exts = (TbExts.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .ToHashSet();
        var sdirs = (TbDirs.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        bool useCreation = cfg.TimeField == "creation";
        var conflict = cfg.Conflict;
        var verify = cfg.Verify;
        var autoEject = cfg.AutoEject;
        var concurrency = cfg.Concurrency;

        try
        {
            sources = sources.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            target = Path.GetFullPath(target);
            foreach (var source in sources)
            {
                if (!Directory.Exists(source)) throw new IOException($"来源目录不存在或无法访问：{source}");
                if (Copier.IsSameOrChild(source, target))
                    throw new IOException("目标目录不能与来源相同，也不能包含来源目录。请选择独立目录，或来源下的新子目录。");
                foreach (var dir in sdirs)
                {
                    if (Path.IsPathRooted(dir) || !Copier.IsSameOrChild(Path.Combine(source, dir), source))
                        throw new IOException($"扫描子目录必须是来源目录内的相对路径：{dir}");
                }
            }
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            await MessageAsync(ex.Message);
            return;
        }

        BtnStart.IsEnabled = false; BtnCancel.IsEnabled = true;
        SettingsPanel.IsEnabled = false; DrivesPanel.IsEnabled = false;
        LbStatus.Text = "扫描中…";
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            int scanErrors = 0;
            Log($"扫描 {sources.Count} 个来源目录…");
            if (sources.Any(s => Copier.IsSameOrChild(target, s)))
                Log($"扫描时排除目标目录：{target}");
            if (sdirs.Length > 0)
            {
                foreach (var d in sources)
                {
                    var missing = sdirs.Where(s => !Directory.Exists(Path.Combine(d, s))).ToList();
                    if (missing.Count == sdirs.Length)
                    {
                        scanErrors++;
                        Log($"提示 {d} 未找到任何扫描子目录（{string.Join(",", sdirs)}）。扫描子目录留空可扫描全部内容。");
                    }
                }
            }
            var files = await Task.Run(() =>
                sources.SelectMany(d => Copier.Scan(d, exts, sdirs, target, ct, message => { scanErrors++; Log(message); }))
                      .DistinctBy(f => Path.GetFullPath(f.Src), StringComparer.OrdinalIgnoreCase)
                      .OrderBy(f => useCreation ? f.Created : f.Modified)
                      .ToList(), ct);
            long totalBytes = files.Sum(f => f.Size);
            int totalFiles = files.Count;
            Log($"待复制 {totalFiles} 个文件，共 {Copier.Sz(totalBytes)}，并发 {concurrency}");
            SetOverall(0, totalFiles, 0, totalBytes);
            SetCurrent(0, 1, "—");
            TbSpeed.Text = "0 B/s";
            if (totalFiles == 0)
            {
                LbStatus.Text = "未找到匹配文件，请检查来源和扫描筛选";
                return;
            }
            var jobs = await Task.Run(() => Copier.PlanCopies(files, target, pattern, useCreation, conflict, ct).ToList(), ct);

            long doneBytes = 0;
            int doneFiles = 0, copied = 0, skipped = 0, failed = 0;
            var toVerify = new System.Collections.Concurrent.ConcurrentDictionary<string, (string src, string dst, long size)>(StringComparer.OrdinalIgnoreCase);
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

            // Overwrites of the same destination run in scan order; different destinations stay parallel.
            await Parallel.ForEachAsync(jobs.GroupBy(j => j.Destination, StringComparer.OrdinalIgnoreCase),
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                (group, token) =>
                {
                    foreach (var job in group)
                    {
                        token.ThrowIfCancellationRequested();
                        var f = job.File;
                        var dst = job.Destination;
                        int slot = System.Threading.Thread.CurrentThread.ManagedThreadId;
                        try
                        {
                            if (job.Skip)
                            {
                                Interlocked.Increment(ref skipped);
                                Interlocked.Add(ref doneBytes, f.Size);
                                var df = Interlocked.Increment(ref doneFiles);
                                Log($"跳过 {f.Src} → {dst}（已存在或本批次同名）");
                                Dispatcher.UIThread.Post(() => SetOverall(df, totalFiles, Interlocked.Read(ref doneBytes), totalBytes));
                                continue;
                            }
                            string action = conflict == "overwrite" && File.Exists(dst) ? "覆盖" : "复制";
                            Log($"{action} {f.Src} → {dst}  ({Copier.Sz(f.Size)})");
                            var srcName = Path.GetFileName(f.Src);
                            active[slot] = (srcName, f.Size, 0);
                            bool ok = Copier.CopyOne(f.Src, dst, n =>
                            {
                                Interlocked.Add(ref doneBytes, n);
                                active.AddOrUpdate(slot,
                                    _ => (srcName, f.Size, n),
                                    (_, cur) => (cur.name, cur.size, cur.done + n));
                            }, token, overwrite: conflict == "overwrite");
                            active.TryRemove(slot, out _);
                            if (ok)
                            {
                                Interlocked.Increment(ref copied);
                                if (verify) toVerify[dst] = (f.Src, dst, f.Size);
                            }
                            else if (token.IsCancellationRequested) Log($"已取消 {f.Src}");
                        }
                        catch (Exception ex) { Interlocked.Increment(ref failed); Log($"失败 {f.Src}: {ex.Message}"); active.TryRemove(slot, out _); }
                        var df2 = Interlocked.Increment(ref doneFiles);
                        Dispatcher.UIThread.Post(() => SetOverall(df2, totalFiles, Interlocked.Read(ref doneBytes), totalBytes));
                    }
                    return ValueTask.CompletedTask;
                });

            speedTimer.Dispose();
            sw.Stop();
            var avg = doneBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.01);
            Dispatcher.UIThread.Post(() => { TbSpeed.Text = Copier.Sz(avg) + "/s"; SetCurrent(1, 1, "—"); });
            Log($"复制完成：复制 {copied}，跳过 {skipped}，失败 {failed}，{Copier.Sz(doneBytes)} in {sw.Elapsed.TotalSeconds:0.0}s（{Copier.Sz(avg)}/s）");

            if (verify && toVerify.Count > 0 && !ct.IsCancellationRequested)
            {
                var vlist = toVerify.Values.ToList();
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
                            else { vbad++; failed++; Log($"校验失败 {dst}  src={a}  dst={b}"); }
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
            LbStatus.Text = ct.IsCancellationRequested ? "已取消" :
                $"完成：复制 {copied}，跳过 {skipped}，失败 {failed}，扫描异常 {scanErrors}";
            if (autoEject && failed == 0 && scanErrors == 0 && !ct.IsCancellationRequested)
            {
                var result = await EjectDrivesAsync(picked.Where(d => !Copier.IsSameOrChild(target, d)));
                if (result.failed > 0) LbStatus.Text += $"；{result.failed} 个盘未确认弹出，详情见日志";
            }
        }
        catch (OperationCanceledException) { Log("已取消"); LbStatus.Text = "已取消"; }
        catch (Exception ex) { Log("错误：" + ex.Message); LbStatus.Text = "失败：" + ex.Message; }
        finally
        {
            BtnStart.IsEnabled = true; BtnCancel.IsEnabled = false;
            SettingsPanel.IsEnabled = true; DrivesPanel.IsEnabled = true;
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
            Title = "提示", Width = 480, SizeToContent = SizeToContent.Height,
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
