using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VibeCopy;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

// ---------- config ----------
class Config
{
    public string Target { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VibeCopy");
    // 通用相机默认：DCIM 通吃，Sony 视频用 M4ROOT/XDROOT/PRIVATE，Canon 用 MISC，Panasonic 用 AVCHD/CLIP 等。
    public string Exts { get; set; } = ".arw,.cr2,.cr3,.nef,.raf,.rw2,.dng,.jpg,.jpeg,.heic,.heif,.mp4,.mov,.mts,.m2ts,.avi,.insv,.xml,.thm";
    public string ScanDirs { get; set; } = "DCIM,PRIVATE,M4ROOT,XDROOT,MISC,AVCHD,CLIP,SSP"; // 空=全盘
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
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ---------- shell eject via COM ----------
static class Shell
{
    public static (bool ok, string msg) Eject(string driveLetter)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application")
                    ?? throw new InvalidOperationException("Shell.Application unavailable");
            dynamic shell = Activator.CreateInstance(t)!;
            var ns = shell.Namespace(17); // ssfDRIVES
            var item = ns?.ParseName(driveLetter.TrimEnd('\\'));
            if (item == null) return (false, "not found");
            item.InvokeVerb("Eject");
            return (true, "ok");
        }
        catch (Exception e) { return (false, e.Message); }
    }
}

// ---------- copy engine ----------
record MediaFile(string Drive, string Src, long Size, DateTime Created, DateTime Modified);

static class Copier
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
                try { fi = new FileInfo(p); }
                catch { continue; }
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
}

// ---------- theme ----------
static class Theme
{
    public static readonly System.Drawing.Color Bg          = System.Drawing.Color.FromArgb(245, 247, 250);
    public static readonly System.Drawing.Color Card        = System.Drawing.Color.White;
    public static readonly System.Drawing.Color Border      = System.Drawing.Color.FromArgb(224, 228, 235);
    public static readonly System.Drawing.Color Text        = System.Drawing.Color.FromArgb(30, 34, 42);
    public static readonly System.Drawing.Color Muted       = System.Drawing.Color.FromArgb(120, 128, 140);
    public static readonly System.Drawing.Color Accent      = System.Drawing.Color.FromArgb(0, 122, 204);
    public static readonly System.Drawing.Color AccentHover = System.Drawing.Color.FromArgb(24, 144, 224);
    public static readonly System.Drawing.Color Danger      = System.Drawing.Color.FromArgb(220, 84, 90);
    public static readonly System.Drawing.Color Track       = System.Drawing.Color.FromArgb(232, 236, 242);
    public static readonly System.Drawing.Color LogBg       = System.Drawing.Color.FromArgb(24, 28, 34);
    public static readonly System.Drawing.Color LogFg       = System.Drawing.Color.FromArgb(220, 226, 235);
    public static readonly System.Drawing.Font  UI          = new("Segoe UI", 9.75f);
    public static readonly System.Drawing.Font  UIBold      = new("Segoe UI Semibold", 9.75f);
    public static readonly System.Drawing.Font  Title       = new("Segoe UI Semibold", 14f);
    public static readonly System.Drawing.Font  Mono        = new("Consolas", 9f);
}

class FlatBtn : Button
{
    public System.Drawing.Color Fill = System.Drawing.Color.FromArgb(236, 240, 246);
    public System.Drawing.Color Hover = System.Drawing.Color.FromArgb(220, 226, 234);
    public System.Drawing.Color Fg = Theme.Text;
    public bool Primary;
    public FlatBtn(string text, bool primary = false)
    {
        Text = text; Primary = primary;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = primary ? 0 : 1;
        FlatAppearance.BorderColor = primary ? Theme.Accent : System.Drawing.Color.FromArgb(170, 180, 195);
        Cursor = Cursors.Hand;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new(14, 2, 14, 2);
        Font = Theme.UIBold; UseVisualStyleBackColor = false;
        if (primary) { Fill = Theme.Accent; Hover = Theme.AccentHover; Fg = System.Drawing.Color.White; }
        Apply();
        MouseEnter += (_, _) => { BackColor = Hover; };
        MouseLeave += (_, _) => { BackColor = Fill; };
        EnabledChanged += (_, _) => Apply();
    }
    void Apply()
    {
        BackColor = Enabled ? Fill : Theme.Track;
        ForeColor = Enabled ? Fg : Theme.Muted;
        FlatAppearance.MouseOverBackColor = Enabled ? Hover : Theme.Track;
        FlatAppearance.MouseDownBackColor = Enabled ? Hover : Theme.Track;
    }
}

class Card : Panel
{
    public string Title { get; set; } = "";
    public Card(string title) { Title = title; BackColor = Theme.Card; Font = Theme.UIBold; ApplyPadding(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ApplyPadding(); }
    void ApplyPadding()
    {
        int top = Theme.UIBold.Height + 14;
        int side = (int)(Font.Height * 0.9);
        Padding = new(side, top, side, side);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new System.Drawing.Pen(Theme.Border);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using var b = new System.Drawing.SolidBrush(Theme.Text);
        g.DrawString(Title, Theme.UIBold, b, Padding.Left, 8);
    }
}

class ProgressPanel : Control
{
    long _val, _max = 1;
    public string Overlay = "就绪";
    public ProgressPanel() { DoubleBuffered = true; Height = 26; BackColor = Theme.Track; Font = Theme.UIBold; ForeColor = Theme.Text; }
    public long Maximum { get => _max; set { _max = System.Math.Max(1, value); Invalidate(); } }
    public long Value { get => _val; set { _val = System.Math.Clamp(value, 0, _max); Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new System.Drawing.Rectangle(0, 0, Width, Height);
        using (var bg = new System.Drawing.SolidBrush(Theme.Track)) g.FillRectangle(bg, r);
        int w = (int)((long)Width * _val / _max);
        if (w > 0)
        {
            var fr = new System.Drawing.Rectangle(0, 0, w, Height);
            using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                fr, Theme.Accent, Theme.AccentHover, 0f);
            g.FillRectangle(grad, fr);
        }
        using var pen = new System.Drawing.Pen(Theme.Border);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        var pct = 100.0 * _val / _max;
        var txt = $"{pct:0.0}%  {Overlay}";
        var sz = g.MeasureString(txt, Font);
        using var fg = new System.Drawing.SolidBrush(w > sz.Width + 16 ? System.Drawing.Color.White : Theme.Text);
        g.DrawString(txt, Font, fg, 10, (Height - sz.Height) / 2);
    }
}

// ---------- UI ----------
class MainForm : Form
{
    readonly Config cfg = Config.Load();
    readonly TextBox tbTarget = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = Theme.UI };
    readonly TextBox tbExts = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = Theme.UI };
    readonly TextBox tbDirs = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = Theme.UI };
    readonly ComboBox cbTime = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.UI, BackColor = Theme.Track };
    readonly ComboBox cbConflict = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.UI, BackColor = Theme.Track };
    readonly CheckBox cbVerify = new() { Text = "复制后校验 (SHA1)", AutoSize = true, Font = Theme.UI, ForeColor = Theme.Text };
    readonly CheckBox cbAutoEject = new() { Text = "完成后自动弹出", AutoSize = true, Font = Theme.UI, ForeColor = Theme.Text };
    readonly ListView lvDrives = new()
    {
        View = View.Details, FullRowSelect = true, CheckBoxes = true,
        Dock = DockStyle.Fill, GridLines = false, BorderStyle = BorderStyle.None,
        Font = Theme.UI, BackColor = Theme.Card
    };
    readonly FlatBtn btnBrowse  = new("浏览…");
    readonly FlatBtn btnRefresh = new("⟳ 刷新");
    readonly FlatBtn btnEject   = new("⏏ 弹出勾选盘");
    readonly FlatBtn btnStart   = new("▶ 开始复制", primary: true);
    readonly FlatBtn btnCancel  = new("■ 取消") { Enabled = false };
    readonly ProgressPanel pb = new() { Dock = DockStyle.Fill };
    readonly Label lbStatus = new()
    {
        Dock = DockStyle.Fill, Text = "就绪", AutoEllipsis = true,
        Font = Theme.UI, ForeColor = Theme.Muted, TextAlign = System.Drawing.ContentAlignment.MiddleLeft
    };
    readonly TextBox tbLog = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Theme.LogBg, ForeColor = Theme.LogFg, Font = Theme.Mono
    };
    CancellationTokenSource? cts;

    public MainForm()
    {
        Text = "VibeCopy — 多卡一键归档";
        Icon = MakeIcon(); ShowIcon = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.UI;
        StartPosition = FormStartPosition.CenterScreen;
        float s = DeviceDpi / 96f;
        int scale(int v) => (int)(v * s);
        ClientSize = new(scale(1280), scale(760));
        MinimumSize = new(scale(1280), scale(720));
        tbTarget.Text = cfg.Target; tbExts.Text = cfg.Exts; tbDirs.Text = cfg.ScanDirs;
        cbTime.Items.AddRange(new[] { "creation", "modified" }); cbTime.SelectedItem = cfg.TimeField;
        cbConflict.Items.AddRange(new[] { "skip", "rename", "overwrite" }); cbConflict.SelectedItem = cfg.Conflict;
        cbVerify.Checked = cfg.Verify;
        cbAutoEject.Checked = cfg.AutoEject;
        lvDrives.Columns.Add("盘符", 90); lvDrives.Columns.Add("卷标", 200);
        lvDrives.Columns.Add("总容量", 110); lvDrives.Columns.Add("可用", 110); lvDrives.Columns.Add("文件系统", 110);

        BuildLayout();
        btnBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = tbTarget.Text };
            if (d.ShowDialog() == DialogResult.OK) tbTarget.Text = d.SelectedPath;
        };
        btnRefresh.Click += (_, _) => RefreshDrives();
        btnEject.Click += (_, _) => EjectChecked();
        btnStart.Click += async (_, _) => await StartAsync();
        btnCancel.Click += (_, _) => cts?.Cancel();
        Load += (_, _) => RefreshDrives();
    }

    void BuildLayout()
    {
        float dpiS = DeviceDpi / 96f;
        int sc(int v) => (int)(v * dpiS);
        int rh = Font.Height;
        int cardPad = Theme.UIBold.Height + 14 + (int)(Font.Height * 0.9);
        int btnH = btnBrowse.PreferredSize.Height;
        int inputH = System.Math.Max(tbTarget.PreferredHeight, btnH);
        int rowH = inputH + 8;
        int cfgRows = 5;
        int cfgH = rowH * cfgRows + cardPad + 12;
        int actH = rowH * 3 + cardPad + 12;
        foreach (var b in new FlatBtn[] { btnBrowse, btnRefresh, btnEject, btnStart, btnCancel })
        { b.MinimumSize = new(0, inputH); b.MaximumSize = new(int.MaxValue, inputH); b.Anchor = AnchorStyles.Left; }
        pb.Height = inputH;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new(16), BackColor = Theme.Bg };
        root.RowStyles.Add(new(SizeType.AutoSize));                // header
        root.RowStyles.Add(new(SizeType.Absolute, cfgH));          // 配置
        root.RowStyles.Add(new(SizeType.Absolute, actH));          // 传输（放前面保证不被挤掉）
        root.RowStyles.Add(new(SizeType.Percent, 45));             // 盘列表
        root.RowStyles.Add(new(SizeType.Percent, 55));             // 日志

        root.Controls.Add(new Label
        {
            Text = "📷  VibeCopy", AutoSize = true, Font = Theme.Title,
            ForeColor = Theme.Text, Margin = new(0, 0, 0, 12)
        });

        // ---- config card ----
        var cfgCard = new Card("归档设置") { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 12) };
        int labW = sc(120), btnW = sc(88);
        var cfgTL = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = cfgRows };
        cfgTL.ColumnStyles.Add(new(SizeType.Absolute, labW));
        cfgTL.ColumnStyles.Add(new(SizeType.Percent, 100));
        cfgTL.ColumnStyles.Add(new(SizeType.Absolute, btnW));
        for (int i = 0; i < cfgRows; i++) cfgTL.RowStyles.Add(new(SizeType.Absolute, rowH));
        btnBrowse.AutoSize = false; btnBrowse.Width = btnW - 8; btnBrowse.Height = inputH;
        tbDirs.PlaceholderText = "空=全盘";

        btnBrowse.Margin = new(8, 3, 0, 3);
        tbTarget.Margin = tbExts.Margin = tbDirs.Margin = cbTime.Margin = cbConflict.Margin = new(0, 3, 0, 3);
        int r = 0;
        cfgTL.Controls.Add(MutedLabel("目标目录"),           0, r);
        cfgTL.Controls.Add(tbTarget,                          1, r);
        cfgTL.Controls.Add(btnBrowse,                         2, r); r++;

        cfgTL.Controls.Add(MutedLabel("扩展名"),             0, r);
        cfgTL.Controls.Add(tbExts,                            1, r); cfgTL.SetColumnSpan(tbExts, 2); r++;

        cfgTL.Controls.Add(MutedLabel("扫描子目录 (空=全盘)"), 0, r);
        cfgTL.Controls.Add(tbDirs,                            1, r); cfgTL.SetColumnSpan(tbDirs, 2); r++;

        // 子文件夹规则 + 同名冲突 同一行
        var ruleTL = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new(0, 3, 0, 3) };
        ruleTL.ColumnStyles.Add(new(SizeType.Percent, 50));
        ruleTL.ColumnStyles.Add(new(SizeType.AutoSize));
        ruleTL.ColumnStyles.Add(new(SizeType.Absolute, rh));
        ruleTL.ColumnStyles.Add(new(SizeType.Percent, 50));
        cbTime.Margin = new(0, 0, 0, 0); cbConflict.Margin = new(0, 0, 0, 0);
        ruleTL.Controls.Add(cbTime, 0, 0);
        var lblConflict = MutedLabel("同名冲突"); lblConflict.Margin = new(0, 0, 8, 0);
        ruleTL.Controls.Add(lblConflict, 1, 0);
        ruleTL.Controls.Add(cbConflict, 3, 0);
        cfgTL.Controls.Add(MutedLabel("子文件夹规则"),        0, r);
        cfgTL.Controls.Add(ruleTL,                            1, r); cfgTL.SetColumnSpan(ruleTL, 2); r++;

        var optFlow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        cbVerify.Margin = new(0, 6, 24, 0);
        cbAutoEject.Margin = new(0, 6, 0, 0);
        optFlow.Controls.AddRange(new Control[] { cbVerify, cbAutoEject });
        cfgTL.Controls.Add(optFlow, 1, r); cfgTL.SetColumnSpan(optFlow, 2);
        cfgCard.Controls.Add(cfgTL);
        root.Controls.Add(cfgCard);

        // ---- action + progress card (置于盘列表之前，防止小窗口时被挤掉) ----
        var actCard = new Card("传输") { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 12) };
        var actTL = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        actTL.ColumnStyles.Add(new(SizeType.Percent, 100));
        actTL.RowStyles.Add(new(SizeType.Absolute, rowH));
        actTL.RowStyles.Add(new(SizeType.Absolute, rowH));
        actTL.RowStyles.Add(new(SizeType.Absolute, rowH));

        var actBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, Margin = new(0, 3, 0, 3) };
        btnStart.Margin = new(0, 0, 8, 0);
        btnCancel.Margin = new(0, 0, 0, 0);
        lbStatus.Margin = new(0, 3, 0, 3);
        pb.Margin = new(0, 3, 0, 3);
        actBtns.Controls.AddRange(new Control[] { btnStart, btnCancel });
        actTL.Controls.Add(actBtns, 0, 0);
        actTL.Controls.Add(pb, 0, 1);
        actTL.Controls.Add(lbStatus, 0, 2);
        actCard.Controls.Add(actTL);
        root.Controls.Add(actCard);

        // ---- drives card ----
        var drivesCard = new Card("可移动盘") { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 12), MinimumSize = new(0, (int)(140 * DeviceDpi / 96f)) };
        var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        mid.ColumnStyles.Add(new(SizeType.Percent, 100));
        mid.ColumnStyles.Add(new(SizeType.AutoSize));
        mid.RowStyles.Add(new(SizeType.Percent, 100));
        mid.Controls.Add(lvDrives, 0, 0);
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new(10, 0, 0, 0) };
        btnRefresh.Margin = new(0, 0, 0, 8);
        side.Controls.AddRange(new Control[] { btnRefresh, btnEject });
        mid.Controls.Add(side, 1, 0);
        drivesCard.Controls.Add(mid);
        root.Controls.Add(drivesCard);

        // ---- log ----
        var logCard = new Card("日志") { Dock = DockStyle.Fill, MinimumSize = new(0, (int)(100 * DeviceDpi / 96f)) };
        logCard.Controls.Add(tbLog);
        root.Controls.Add(logCard);

        Controls.Add(root);
    }

    static Label MutedLabel(string s) => new()
    {
        Text = s, AutoSize = false, Font = Theme.UI, ForeColor = Theme.Muted,
        Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Width = 160, Padding = new(0, 0, 10, 0)
    };

    void RefreshDrives()
    {
        lvDrives.Items.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
        {
            string label = "", fs = ""; long total = 0, free = 0;
            try { if (d.IsReady) { label = d.VolumeLabel; fs = d.DriveFormat; total = d.TotalSize; free = d.AvailableFreeSpace; } }
            catch { }
            var it = new ListViewItem(new[] { d.Name, label, Sz(total), Sz(free), fs }) { Tag = d.Name, Checked = true };
            lvDrives.Items.Add(it);
        }
    }

    IEnumerable<string> CheckedDrives() =>
        lvDrives.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!);

    void EjectChecked()
    {
        foreach (var d in CheckedDrives().ToList())
        {
            var (ok, msg) = Shell.Eject(d);
            Log($"弹出 {d}: {(ok ? "OK" : msg)}");
        }
        RefreshDrives();
    }

    async Task StartAsync()
    {
        var drives = CheckedDrives().ToList();
        if (drives.Count == 0) { MessageBox.Show("请勾选至少一个盘"); return; }
        var target = tbTarget.Text.Trim();
        if (string.IsNullOrEmpty(target)) { MessageBox.Show("请设置目标目录"); return; }
        Directory.CreateDirectory(target);

        cfg.Target = target; cfg.Exts = tbExts.Text; cfg.ScanDirs = tbDirs.Text;
        cfg.TimeField = (string)cbTime.SelectedItem!;
        cfg.Conflict = (string)cbConflict.SelectedItem!;
        cfg.Verify = cbVerify.Checked; cfg.AutoEject = cbAutoEject.Checked;
        cfg.Save();

        var exts = tbExts.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();
        var sdirs = tbDirs.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
        bool useCreation = cfg.TimeField == "creation";

        btnStart.Enabled = false; btnCancel.Enabled = true;
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            Log($"扫描 {drives.Count} 个盘…");
            var files = await Task.Run(() =>
                drives.SelectMany(d => Copier.Scan(d, exts, sdirs))
                      .OrderBy(f => useCreation ? f.Created : f.Modified)
                      .ToList(), ct);
            long total = files.Sum(f => f.Size);
            Log($"待复制 {files.Count} 个文件，共 {Sz(total)}");
            pb.Maximum = System.Math.Max(1, total); pb.Value = 0; pb.Overlay = $"0 / {Sz(total)}"; pb.Invalidate();

            long done = 0; int copied = 0, skipped = 0, failed = 0;
            var sw = Stopwatch.StartNew();
            long lastTickBytes = 0; var lastTick = sw.Elapsed;

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
                                Log($"跳过 {f.Src} → {dst}（已存在）"); Report(); continue;
                            }
                            if (cfg.Conflict == "rename") { dst = UniquePath(dst); action = "改名"; }
                            else action = "覆盖";
                        }
                        Log($"{action} {f.Src} → {dst}  ({Sz(f.Size)})");

                        bool ok = Copier.CopyOne(f.Src, dst, n =>
                        {
                            Interlocked.Add(ref done, n);
                            var now = sw.Elapsed;
                            if ((now - lastTick).TotalMilliseconds >= 200)
                            {
                                var speed = (done - lastTickBytes) / (now - lastTick).TotalSeconds;
                                lastTick = now; lastTickBytes = done;
                                ReportSpeed(speed, f.Src);
                            }
                        }, ct);
                        if (ok)
                        {
                            if (cfg.Verify)
                            {
                                var a = Sha1(f.Src); var b = Sha1(dst);
                                if (a != b) { failed++; Log($"校验失败 {f.Src} → {dst}"); try { File.Delete(dst); } catch { } continue; }
                            }
                            copied++;
                        }
                    }
                    catch (Exception e) { failed++; Log($"失败 {f.Src}: {e.Message}"); }
                    Report();

                    void Report()
                    {
                        long d = done;
                        BeginInvoke(() => { pb.Value = d; pb.Invalidate(); });
                    }
                    void ReportSpeed(double sp, string name)
                    {
                        long d = done;
                        var el = sw.Elapsed.TotalSeconds;
                        var over = $"{Sz(d)} / {Sz(total)}   •   {Sz(sp)}/s   •   {el:0}s";
                        var nm = Path.GetFileName(name);
                        BeginInvoke(() =>
                        {
                            pb.Value = d; pb.Overlay = over; pb.Invalidate();
                            lbStatus.Text = "正在复制  " + nm;
                        });
                    }
                }
            }, ct);

            sw.Stop();
            var avg = done / Math.Max(sw.Elapsed.TotalSeconds, 0.01);
            Log($"完成：复制 {copied}，跳过 {skipped}，失败 {failed}，{Sz(done)} in {sw.Elapsed.TotalSeconds:0.0}s（{Sz(avg)}/s）");
            if (cfg.AutoEject && failed == 0 && !ct.IsCancellationRequested)
            {
                foreach (var d in drives)
                {
                    var (ok, msg) = Shell.Eject(d);
                    Log($"弹出 {d}: {(ok ? "OK" : msg)}");
                }
            }
        }
        catch (OperationCanceledException) { Log("已取消"); }
        catch (Exception e) { Log("错误：" + e.Message); }
        finally
        {
            btnStart.Enabled = true; btnCancel.Enabled = false;
            cts?.Dispose(); cts = null;
            RefreshDrives();
        }
    }

    void Log(string s)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}";
        try { File.AppendAllText(Config.LogPath, line + Environment.NewLine); } catch { }
        void append() { tbLog.AppendText(line + "\r\n"); }
        if (InvokeRequired) BeginInvoke(append); else append();
    }

    static string Sz(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0; while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return $"{n:0.#}{u[i]}";
    }

    static string UniquePath(string p)
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

    static string Sha1(string path)
    {
        using var s = File.OpenRead(path);
        using var h = SHA1.Create();
        return Convert.ToHexString(h.ComputeHash(s));
    }

    static System.Drawing.Icon MakeIcon()
    {
        using var bmp = new System.Drawing.Bitmap(64, 64);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, 64, 64), Theme.Accent, Theme.AccentHover, 45f);
            g.FillEllipse(bg, 2, 2, 60, 60);
            using var fnt = new System.Drawing.Font("Segoe UI Emoji", 28f, System.Drawing.FontStyle.Regular);
            using var fg = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            var sf = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            g.DrawString("📷", fnt, fg, new System.Drawing.RectangleF(0, 2, 64, 64), sf);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
