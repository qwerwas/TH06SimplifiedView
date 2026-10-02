using System.Diagnostics;

namespace TH06SimplifiedView;

public sealed class MainForm : Form
{
    private readonly Th06Reader _reader = new();
    private readonly RenderPanel _render;
    private readonly InfoPanel _info;
    private readonly ToolStripStatusLabel _statusLabel = new();
    private volatile int _fpsCap;   // 渲染帧率上限, 0 = 无限制 (默认)
    private long _lastInfoUpdate;
    private long _nextFrameTicks;   // 限帧: 下一帧允许执行的时刻 (Stopwatch ticks)

    private ToolStripMenuItem _miTopmost = null!;
    private ToolStripMenuItem _miGraze = null!;
    private ToolStripMenuItem _miPlayerBullets = null!;
    private ToolStripMenuItem _miItems = null!;
    private ToolStripMenuItem _miShootable = null!;
    private ToolStripMenuItem _miLasers = null!;
    private ToolStripMenuItem _miVersionCheck = null!;

    public MainForm()
    {
        Text = "TH06 简化显示 by Mrrty1";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 520);

        // 窗口/任务栏图标 = exe 内嵌图标 (csproj ApplicationIcon), 与文件图标统一
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---- 菜单 ----
        var menu = new MenuStrip();
        var miOptions = new ToolStripMenuItem("选项(&O)");

        _miTopmost = new ToolStripMenuItem("窗口置顶", null, (s, e) =>
        {
            TopMost = _miTopmost!.Checked;
        }) { CheckOnClick = true, Checked = true };
        TopMost = true;

        _miGraze = new ToolStripMenuItem("显示擦弹范围", null, (s, e) => _render.ShowGraze = _miGraze!.Checked)
        { CheckOnClick = true, Checked = false };
        _miPlayerBullets = new ToolStripMenuItem("显示自机弹", null, (s, e) => _render.ShowPlayerBullets = _miPlayerBullets!.Checked)
        { CheckOnClick = true, Checked = true };
        _miItems = new ToolStripMenuItem("显示道具", null, (s, e) => _render.ShowItems = _miItems!.Checked)
        { CheckOnClick = true, Checked = true };
        _miShootable = new ToolStripMenuItem("显示敌人可击打范围", null, (s, e) => _render.ShowShootable = _miShootable!.Checked)
        { CheckOnClick = true, Checked = false };
        _miLasers = new ToolStripMenuItem("显示激光", null, (s, e) => _render.ShowLasers = _miLasers!.Checked)
        { CheckOnClick = true, Checked = true };

        // 错位检测默认关: v1.02 等近似版本也能正常读, 模块大小不符≠数据错位, 不用眼不见心不烦
        _miVersionCheck = new ToolStripMenuItem("版本错位检测", null, (s, e) =>
        {
            _reader.VersionCheckEnabled = _miVersionCheck!.Checked;
            _reader.RecheckVersion(); // 已连接时立即按新开关重新验证, 不必断开重连
        })
        { CheckOnClick = true, Checked = false };

        var miHz = new ToolStripMenuItem("读取频率");
        foreach (int hz in new[] { 30, 60, 120 })
        {
            var item = new ToolStripMenuItem(hz + " Hz", null, (s, e) =>
            {
                _reader.PollHz = hz;
                if (s is ToolStripMenuItem self)
                    foreach (ToolStripMenuItem it in miHz.DropDownItems) it.Checked = ReferenceEquals(it, self);
            }) { Checked = hz == 60 };
            miHz.DropDownItems.Add(item);
        }

        var miFps = new ToolStripMenuItem("渲染帧率");
        foreach ((string label, int fps) in new[] { ("无限制 (默认)", 0), ("60 FPS", 60), ("120 FPS", 120), ("240 FPS", 240) })
        {
            var item = new ToolStripMenuItem(label, null, (s, e) =>
            {
                _fpsCap = fps;
                if (s is ToolStripMenuItem self)
                    foreach (ToolStripMenuItem it in miFps.DropDownItems) it.Checked = ReferenceEquals(it, self);
            }) { Checked = fps == 0 };
            miFps.DropDownItems.Add(item);
        }

        var miReattach = new ToolStripMenuItem("重新附加进程", null, (s, e) => _reader.RequestReattach());

        miOptions.DropDownItems.Add(_miTopmost);
        miOptions.DropDownItems.Add(_miGraze);
        miOptions.DropDownItems.Add(_miPlayerBullets);
        miOptions.DropDownItems.Add(_miItems);
        miOptions.DropDownItems.Add(_miShootable);
        miOptions.DropDownItems.Add(_miLasers);
        miOptions.DropDownItems.Add(new ToolStripSeparator());
        miOptions.DropDownItems.Add(_miVersionCheck);
        miOptions.DropDownItems.Add(miHz);
        miOptions.DropDownItems.Add(miFps);
        miOptions.DropDownItems.Add(miReattach);
        menu.Items.Add(miOptions);
        MainMenuStrip = menu;

        // ---- 面板 ----
        // 宽 232: 最长图例文字 (约 200px @ 9pt YaHei) 在 200 宽面板下被右边裁掉 (放大窗口也没用, 面板宽固定)
        _info = new InfoPanel { Dock = DockStyle.Right, Width = 232, Padding = new Padding(8, 8, 8, 8) };
        _render = new RenderPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 13, 18) };

        Controls.Add(_render);
        Controls.Add(_info);
        Controls.Add(menu);

        // ---- 状态栏 ----
        var status = new StatusStrip();
        status.Items.Add(_statusLabel);
        Controls.Add(status);

        Size = new Size(768 + 232 + 16, 896 + 24 + 40 + 80);

        // 渲染循环由 Program.cs 的 DoEvents 主循环驱动 (每轮调用 RenderFrame, 见其注释)

        FormClosed += (s, e) => _reader.Dispose();
    }

    /// <summary>
    /// 渲染一帧, 由 Program.cs 的 DoEvents 主循环每轮调用。
    /// 为什么不用 System.Windows.Forms.Timer: 精度受系统时钟 ~15.6ms 分辨率限制, 实测上限 ~40fps。
    /// 为什么不用 async/Task.Yield 自投递循环: 每帧向消息队列回投 continuation, 高帧率下饿死消息泵,
    /// 窗口表现为"无响应"。
    /// 最终方案: DoEvents 主循环 + 本方法内 Thread.Sleep 限帧 (配合 timeBeginPeriod(1), 睡眠精度 1ms)。
    /// </summary>
    internal void RenderFrame()
    {
        if (IsDisposed || Disposing) return;

        if (_fpsCap > 0)
        {
            // 限帧: 睡到下一帧时刻 (整毫秒部分睡, 亚毫秒部分自旋), 不占满 CPU
            long interval = Stopwatch.Frequency / _fpsCap;
            long now = Stopwatch.GetTimestamp();
            long wait = _nextFrameTicks - now;
            if (wait > 0)
            {
                int sleepMs = (int)(wait * 1000 / Stopwatch.Frequency);
                if (sleepMs > 0) Thread.Sleep(sleepMs);
                while (Stopwatch.GetTimestamp() < _nextFrameTicks) Thread.SpinWait(32);
            }
            _nextFrameTicks += interval;
            if (_nextFrameTicks < now) _nextFrameTicks = now + interval; // 卡顿后重新同步, 避免连发补帧
        }
        else
        {
            Thread.Sleep(0); // 无限制: 让出同核就绪线程, 稍降 CPU 占用
        }

        if (_render.IsHandleCreated)
        {
            _render.Snapshot = _reader.Latest;
            _render.Invalidate();
            _render.Update(); // 强制同步重绘, 不排队等消息循环
        }
        if (Environment.TickCount64 - _lastInfoUpdate >= 100)
        {
            _lastInfoUpdate = Environment.TickCount64; // 信息面板 10Hz 刷新即可, 省下无限制模式的绘制开销
            UpdateInfo();
        }
    }

    private void UpdateInfo()
    {
        var snap = _reader.Latest;
        string stale = snap.Valid && Environment.TickCount64 - snap.TimestampMs > 2000 ? " (数据已停滞!)" : "";
        _statusLabel.Text = $"{_reader.AttachStatus} | 读取 {_reader.ActualHz:F0} Hz{stale}";

        _info.SetData(new InfoPanel.InfoData
        {
            Valid = snap.Valid,
            Attached = _reader.Attached,
            VersionWarning = _reader.VersionWarning,
            Difficulty = snap.Difficulty,
            Score = snap.Score,
            PlayerState = snap.PlayerState,
            Focus = snap.PlayerFocus,
            PlayerX = snap.PlayerX,
            PlayerY = snap.PlayerY,
            HitR = snap.PlayerHitR,
            BulletDanger = CountState(snap, 1),
            BulletSpawning = CountSpawning(snap),
            EnemyCount = snap.EnemyCount,
            LaserCount = snap.LaserCount,
            BossInfo = BossInfo(snap),
            RenderFps = _render.Fps,
            PollHz = _reader.ActualHz,
        });
    }

    private static int CountState(Th06Snapshot s, int st)
    {
        int n = 0;
        for (int i = 0; i < s.BulletCount; i++) if (s.Bullets[i].State == st) n++;
        return n;
    }

    private static int CountSpawning(Th06Snapshot s)
    {
        int n = 0;
        for (int i = 0; i < s.BulletCount; i++) if (s.Bullets[i].State is >= 2 and <= 4) n++;
        return n;
    }

    private static string BossInfo(Th06Snapshot s)
    {
        for (int i = 0; i < s.EnemyCount; i++)
        {
            if (s.Enemies[i].IsBoss)
            {
                var e = s.Enemies[i];
                float pct = e.MaxLife > 0 ? 100f * e.Life / e.MaxLife : 0;
                return $"HP {e.Life} / {e.MaxLife} ({pct:F0}%)";
            }
        }
        return "—";
    }
}

/// <summary>右侧信息面板。</summary>
internal sealed class InfoPanel : Panel
{
    public struct InfoData
    {
        public bool Valid, Attached, Focus;
        public string VersionWarning, BossInfo;
        public int Difficulty, Score, PlayerState;
        public float PlayerX, PlayerY, HitR;
        public int BulletDanger, BulletSpawning, EnemyCount, LaserCount;
        public double RenderFps, PollHz;
    }

    private InfoData _d;
    private readonly Font _font = new("Microsoft YaHei UI", 9F);
    private readonly Font _fontBig = new("Consolas", 10F);

    public void SetData(InfoData d) { _d = d; Invalidate(); }

    public InfoPanel()
    {
        BackColor = Color.FromArgb(20, 20, 27);
        DoubleBuffered = true;
        Resize += (s, e) => Invalidate();
        Paint += Draw;
    }

    private static readonly string[] DiffNames = { "Easy", "Normal", "Hard", "Lunatic", "Extra" };
    private static readonly string[] PStateNames = { "存活", "重生中", "死亡", "无敌" };

    private void Draw(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        float y = 12;
        // 换行宽度 = 面板宽 - 左右留白; DrawString 用 RectangleF 布局超宽自动折行, 不再被右边缘裁掉
        int wrapW = (int)(ClientSize.Width - 16);

        void Line(string label, string value, Brush? vb = null)
        {
            g.DrawString(label, _font, Brushes.Gray, 8, y);
            var size = g.MeasureString(value, _fontBig, wrapW);
            g.DrawString(value, _fontBig, vb ?? Brushes.WhiteSmoke, new RectangleF(8, y + 16, wrapW, size.Height + 4));
            y += 16 + Math.Max(26, size.Height + 2);
        }
        void Head(string t)
        {
            g.DrawString(t, _font, Brushes.DimGray, 8, y);
            y += 20;
        }

        if (!_d.Attached)
        {
            g.DrawString("等待游戏进程…\n请启动 th06 ", _fontBig, Brushes.Orange, 8, y);
            return;
        }

        if (_d.VersionWarning.Length > 0)
        {
            var size = g.MeasureString("⚠ " + _d.VersionWarning, _font, wrapW);
            g.DrawString("⚠ " + _d.VersionWarning, _font, Brushes.Red, new RectangleF(8, y, wrapW, size.Height + 4));
            y += Math.Max(40, size.Height + 6);
        }

        if (!_d.Valid) { g.DrawString("已连接, 等待数据…", _fontBig, Brushes.Gray, 8, y); return; }

        Head("── 对局 ──");
        Line("难度", _d.Difficulty is >= 0 and <= 4 ? DiffNames[_d.Difficulty] : "?");
        Line("得分", _d.Score.ToString("N0"));
        Line("Boss", _d.BossInfo);

        Head("── 自机 ──");
        Line("状态", PStateNames[Math.Clamp(_d.PlayerState, 0, 3)] + (_d.Focus ? "  低速" : ""), _d.PlayerState == 0 ? Brushes.WhiteSmoke : Brushes.Orange);
        Line("坐标", $"({_d.PlayerX:F0}, {_d.PlayerY:F0})");
        Line("判定半径", $"{_d.HitR:F1}");

        Head("── 实体 ──");
        Line("敌弹 (危险)", _d.BulletDanger.ToString(), _d.BulletDanger > 0 ? Brushes.OrangeRed : Brushes.WhiteSmoke);
        Line("敌弹 (生成中)", _d.BulletSpawning.ToString(), Brushes.DarkOrange);
        Line("敌人", _d.EnemyCount.ToString());
        Line("激光", _d.LaserCount.ToString());

        Head("── 工具 ──");
        Line("渲染帧率", $"{_d.RenderFps:F0} FPS");
        Line("读取频率", $"{_d.PollHz:F0} Hz");

        // 图例
        y += 8;
        Head("── 图例 ──");
        void Legend(Brush b, string t, bool circle = true)
        {
            if (circle) g.FillEllipse(b, 10, y + 3, 10, 10);
            else g.FillRectangle(b, 10, y + 3, 10, 10);
            int legendW = (int)(ClientSize.Width - 36);
            var size = g.MeasureString(t, _font, legendW);
            g.DrawString(t, _font, Brushes.Silver, new RectangleF(28, y, legendW, size.Height + 4));
            y += Math.Max(20, size.Height + 2); // 超宽折行时按实测高度推进, 行间距不打架
        }
        Legend(new SolidBrush(Color.FromArgb(255, 50, 50)), "激光红带 = 致死区 ", false);
        Legend(new SolidBrush(Color.FromArgb(255, 200, 80)), "黄虚线 = 激光蓄力轨迹 (无判定)", false);
        Legend(new SolidBrush(Color.FromArgb(255, 77, 109)), "敌弹: 飞行中 (有判定)");
        Legend(new SolidBrush(Color.FromArgb(255, 166, 48)), "敌弹: 生成动画 (无判定)");
        Legend(new SolidBrush(Color.FromArgb(122, 122, 133)), "敌弹: 消散中");
        Legend(new SolidBrush(Color.FromArgb(255, 90, 90)), "敌人红框 = 撞死判定 ", false);
        Legend(new SolidBrush(Color.FromArgb(255, 130, 130)), "红点状框 = 无敌演出 ", false);
        Legend(new SolidBrush(Color.FromArgb(255, 215, 0)), "敌人黄虚线 = 可击打范围 ", false);
        Legend(new SolidBrush(Color.FromArgb(34, 211, 238)), "自机弹");
        Legend(new SolidBrush(Color.FromArgb(124, 252, 0)), "道具", false);
        Legend(Brushes.White, "自机判定");
    }
}

/// <summary>游戏区渲染面板: 把 384x448 的游戏坐标系放大绘制纯色判定点。</summary>
internal sealed class RenderPanel : Panel
{
    public Th06Snapshot? Snapshot { get; set; }
    public bool ShowGraze, ShowPlayerBullets = true, ShowItems = true, ShowShootable, ShowLasers = true;

    public double Fps;

    private int _frames;
    private long _fpsWindow;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private static readonly SolidBrush BrushBulletFired = new(Color.FromArgb(255, 77, 109));
    private static readonly SolidBrush BrushBulletSpawn = new(Color.FromArgb(255, 166, 48));
    private static readonly SolidBrush BrushBulletDespawn = new(Color.FromArgb(122, 122, 133));
    private static readonly SolidBrush BrushEnemyKill = new(Color.FromArgb(90, 20, 20));
    private static readonly Pen PenEnemyKill = new(Color.FromArgb(255, 90, 90));
    private static readonly Pen PenShootable = new(Color.FromArgb(160, 255, 215, 0))
    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
    private static readonly Pen PenBoss = new(Color.FromArgb(255, 60, 60), 2F);
    private static readonly SolidBrush BrushPlayerBullet = new(Color.FromArgb(34, 211, 238));
    private static readonly Pen PenPlayerHitbox = new(Color.White, 1.5F);
    private static readonly SolidBrush BrushPlayerCore = new(Color.FromArgb(255, 42, 42));
    private static readonly Pen PenInvuln = new(Color.FromArgb(80, 250, 123)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
    private static readonly Pen PenGraze = new(Color.FromArgb(60, 80, 250, 123));
    private static readonly SolidBrush BrushFieldBg = new(Color.FromArgb(20, 20, 28));
    private static readonly Pen PenLaserCharge = new(Color.FromArgb(190, 255, 200, 80), 1.6F)
    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
    private static readonly Pen PenLaserFaded = new(Color.FromArgb(70, 150, 150, 160), 1.2F);
    private static readonly Pen PenLaserVisual = new(Color.FromArgb(45, 255, 255, 255), 1F);
    private static readonly Pen PenLaserDeadly = new(Color.FromArgb(255, 110, 110), 1.6F);
    private static readonly SolidBrush BrushLaserDeadly = new(Color.FromArgb(80, 255, 50, 50));
    private static readonly Pen PenEnemyInvuln = new(Color.FromArgb(255, 130, 130))
    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
    private static readonly SolidBrush BrushItemPower = new(Color.FromArgb(60, 200, 60));
    private static readonly SolidBrush BrushItemPoint = new(Color.FromArgb(77, 166, 255));
    private static readonly SolidBrush BrushItemBomb = new(Color.FromArgb(176, 190, 197));
    private static readonly SolidBrush BrushItemFull = new(Color.FromArgb(255, 235, 59));
    private static readonly SolidBrush BrushItemLife = new(Color.FromArgb(255, 82, 82));
    private static readonly SolidBrush BrushItemOther = new(Color.FromArgb(224, 224, 224));

    private readonly Font _font = new("Consolas", 9F);

    public RenderPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor); // 整面板深色底 (含区域框外), 与游戏区背景区分
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 计算缩放与居中
        float scale = Math.Min(ClientSize.Width / Th06.REGION_W, ClientSize.Height / Th06.REGION_H);
        float ox = (ClientSize.Width - Th06.REGION_W * scale) / 2;
        float oy = (ClientSize.Height - Th06.REGION_H * scale) / 2;
        float GX(float x) => ox + x * scale;
        float GY(float y) => oy + y * scale;

        float fw = Th06.REGION_W * scale, fh = Th06.REGION_H * scale;

        // 游戏区背景 (384x448)
        g.FillRectangle(BrushFieldBg, ox, oy, fw, fh);

        // 之后所有实体都裁剪在游戏区内: 超出区域框的部分一律不显示
        g.SetClip(new RectangleF(ox, oy, fw, fh));

        var snap = Snapshot;
        if (snap is not { Valid: true })
        {
            g.DrawString("等待游戏进程与数据…", _font, Brushes.Gray, ox + 8, oy + 8);
            DrawFieldBorder(g, ox, oy, fw, fh);
            return;
        }

        // FPS 统计
        _frames++;
        if (_sw.ElapsedMilliseconds - _fpsWindow >= 1000)
        {
            Fps = _frames * 1000.0 / (_sw.ElapsedMilliseconds - _fpsWindow);
            _frames = 0;
            _fpsWindow = _sw.ElapsedMilliseconds;
        }

        // ---- 道具 ----
        if (ShowItems)
        {
            for (int i = 0; i < snap.ItemCount; i++)
            {
                ref readonly var it = ref snap.Items[i];
                var b = it.Type switch
                {
                    0 or 2 => BrushItemPower,
                    1 => BrushItemPoint,
                    3 => BrushItemBomb,
                    4 => BrushItemFull,
                    5 => BrushItemLife,
                    _ => BrushItemOther,
                };
                float s = it.Type is 2 or 4 or 5 ? 3.5f : 2.5f;
                g.FillRectangle(b, GX(it.X) - s, GY(it.Y) - s, s * 2, s * 2);
            }
        }

        // ---- 激光: 黄虚线 = 蓄力轨迹(无判定), 红带 = 真实致死区 (垂直宽 = 视觉宽的一半) ----
        if (ShowLasers)
        {
            for (int i = 0; i < snap.LaserCount; i++)
            {
                ref readonly var lz = ref snap.Lasers[i];
                float ax = GX(lz.X0), ay = GY(lz.Y0), bx = GX(lz.X1), by = GY(lz.Y1);

                if (lz.State == 0)
                {
                    g.DrawLine(PenLaserCharge, ax, ay, bx, by);
                }
                else if (lz.State == 2)
                {
                    g.DrawLine(PenLaserFaded, ax, ay, bx, by);
                }
                else
                {
                    // 喷射中: 画出完整视觉宽度轮廓, 对照内部致死带有多"瘦"
                    float vdx = bx - ax, vdy = by - ay;
                    float vlen = MathF.Sqrt(vdx * vdx + vdy * vdy);
                    if (vlen > 0.001f)
                    {
                        vdx /= vlen; vdy /= vlen;
                        float vpx = -vdy, vpy = vdx;
                        float vw = lz.HalfW * 2 * scale; // 致死半宽*2 = 视觉半宽 (width/2)
                        g.DrawPolygon(PenLaserVisual, new PointF[]
                        {
                            new(bx + vpx * vw, by + vpy * vw),
                            new(bx - vpx * vw, by - vpy * vw),
                            new(ax - vpx * vw, ay - vpy * vw),
                            new(ax + vpx * vw, ay + vpy * vw),
                        });
                    }
                }

                if (lz.Deadly)
                {
                    float dx = bx - ax, dy = by - ay;
                    float len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len > 0.001f)
                    {
                        dx /= len; dy /= len;
                        float pX = -dy, pY = dx;
                        float cX = GX(lz.DeadlyCx), cY = GY(lz.DeadlyCy);
                        float hl = lz.DeadlyHalfLen * scale, hw = lz.HalfW * scale;
                        var poly = new PointF[]
                        {
                            new(cX + dx * hl + pX * hw, cY + dy * hl + pY * hw),
                            new(cX + dx * hl - pX * hw, cY + dy * hl - pY * hw),
                            new(cX - dx * hl - pX * hw, cY - dy * hl - pY * hw),
                            new(cX - dx * hl + pX * hw, cY - dy * hl + pY * hw),
                        };
                        g.FillPolygon(BrushLaserDeadly, poly);
                        g.DrawPolygon(PenLaserDeadly, poly);
                    }
                }
            }
        }

        // ---- 敌人: 实线红框 = 撞死判定 (hitboxDimensions/1.5), 虚线黄框 = 可击打判定 (hitboxDimensions, 需开启) ----
        for (int i = 0; i < snap.EnemyCount; i++)
        {
            ref readonly var en = ref snap.Enemies[i];
            if (en.IsInvisible) continue;

            // 可击打范围 (可选, 淡黄虚线)
            if (ShowShootable)
            {
                float sw = Math.Max(en.ShootHalfW, 2);
                float sh = Math.Max(en.ShootHalfH, 2);
                g.DrawRectangle(PenShootable, GX(en.X - sw), GY(en.Y - sh), sw * 2 * scale, sh * 2 * scale);
            }

            // 撞死判定 (危险区, 只有 isCollidable 的敌人才撞人)
            if (en.IsCollidable)
            {
                float kw = Math.Max(en.KillHalfW, 1.5f);
                float kh = Math.Max(en.KillHalfH, 1.5f);
                if (en.IsDamageable)
                {
                    g.FillRectangle(BrushEnemyKill, GX(en.X - kw), GY(en.Y - kh), kw * 2 * scale, kh * 2 * scale);
                    g.DrawRectangle(en.IsBoss ? PenBoss : PenEnemyKill, GX(en.X - kw), GY(en.Y - kh), kw * 2 * scale, kh * 2 * scale);
                }
                else
                {
                    // 无敌演出 (如道中 Boss 被打剩 1 HP 退场): 点状空心框, 不填充
                    g.DrawRectangle(PenEnemyInvuln, GX(en.X - kw), GY(en.Y - kh), kw * 2 * scale, kh * 2 * scale);
                }
            }

            // 中心点
            g.FillEllipse(Brushes.Gold, GX(en.X) - 1.5F, GY(en.Y) - 1.5F, 3, 3);
        }

        // ---- Boss 血条 ----
        for (int i = 0; i < snap.EnemyCount; i++)
        {
            ref readonly var en = ref snap.Enemies[i];
            if (!en.IsBoss || en.MaxLife <= 0) continue;
            float barW = Th06.REGION_W * scale * 0.7F;
            float bx = ox + (Th06.REGION_W * scale - barW) / 2;
            float by = oy + 4;
            g.DrawRectangle(PenBoss, bx, by, barW, 6);
            g.FillRectangle(Brushes.Red, bx + 1, by + 1, (barW - 2) * Math.Clamp(en.Life / (float)en.MaxLife, 0, 1), 4);
            break;
        }

        // ---- 自机弹 ----
        if (ShowPlayerBullets)
        {
            for (int i = 0; i < snap.PlayerBulletCount; i++)
            {
                ref readonly var pb = ref snap.PlayerBullets[i];
                g.FillEllipse(BrushPlayerBullet, GX(pb.X) - 1.5F, GY(pb.Y) - 1.5F, 3, 3);
            }
        }

        // ---- 敌弹 (按真实判定半径绘制) ----
        for (int i = 0; i < snap.BulletCount; i++)
        {
            ref readonly var b = ref snap.Bullets[i];
            float r = b.R * scale;
            var brush = b.State switch
            {
                1 => BrushBulletFired,
                >= 2 and <= 4 => BrushBulletSpawn,
                _ => BrushBulletDespawn,
            };
            if (b.State == 1 && r < 1.5F) r = 1.5F; // 危险弹保证可见
            g.FillEllipse(brush, GX(b.X) - r, GY(b.Y) - r, r * 2, r * 2);

            if (ShowGraze && b.State == 1 && b.GrazeR > b.R)
            {
                float gr = b.GrazeR * scale;
                g.DrawEllipse(PenGraze, GX(b.X) - gr, GY(b.Y) - gr, gr * 2, gr * 2);
            }
        }

        // ---- 自机 ----
        float px = GX(snap.PlayerX), py = GY(snap.PlayerY);
        float hr = Math.Clamp(snap.PlayerHitR, 1.5F, 12F) * scale;
        if (snap.PlayerState == 2) // 死亡: 淡显
        {
            g.FillEllipse(new SolidBrush(Color.FromArgb(90, 255, 255, 255)), px - hr, py - hr, hr * 2, hr * 2);
        }
        else
        {
            g.FillEllipse(Brushes.White, px - hr, py - hr, hr * 2, hr * 2);
        }
        g.FillEllipse(BrushPlayerCore, px - 2, py - 2, 4, 4);
        if (snap.PlayerState == 3) g.DrawEllipse(PenInvuln, px - hr - 4, py - hr - 4, (hr + 4) * 2, (hr + 4) * 2);
        if (snap.PlayerFocus)
        {
            g.DrawLine(PenPlayerHitbox, px - hr - 3, py, px + hr + 3, py);
            g.DrawLine(PenPlayerHitbox, px, py - hr - 3, px, py + hr + 3);
        }

        DrawFieldBorder(g, ox, oy, fw, fh);
    }

    /// <summary>区域框: 外层暗色粗环 + 内层亮色细线。ResetClip 后绘制, 不被区域裁剪, 永远完整可见。</summary>
    private static void DrawFieldBorder(Graphics g, float ox, float oy, float fw, float fh)
    {
        g.ResetClip();
        using var penOuter = new Pen(Color.FromArgb(64, 64, 84), 4F);
        g.DrawRectangle(penOuter, ox - 3, oy - 3, fw + 6, fh + 6);
        using var penInner = new Pen(Color.FromArgb(188, 188, 208), 1.6F);
        g.DrawRectangle(penInner, ox, oy, fw, fh);
    }
}
