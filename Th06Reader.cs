using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TH06SimplifiedView;

/// <summary>
/// 通过 ReadProcessMemory 只读附加到 th06 (v1.02h) 进程, 按固定频率采样快照。
/// 不写入游戏内存、不注入 DLL —— 纯外部只读, 对游戏零侵入。
/// </summary>
public sealed class Th06Reader : IDisposable
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _hProcess = IntPtr.Zero;
    private int _pid = -1;
    private string _procName = "";
    private uint _baseAddr = Th06.DEFAULT_IMAGE_BASE; // 2002 年 exe 无 ASLR, 基址恒为 0x400000

    private readonly Thread _thread;
    private volatile bool _running = true;
    private readonly object _sync = new();

    public volatile Th06Snapshot Latest = new();
    public int PollHz = 60;

    // 状态信息 (UI 线程读取)
    public string AttachStatus = "未连接";
    public bool Attached => _hProcess != IntPtr.Zero;
    public string ProcessName => _procName;
    public int ProcessId => _pid;
    public double ActualHz;
    public string VersionWarning = "";

    // 错位检测开关 (默认关): 实测 v1.02 (模块 3051520) 等近似版本地址表也兼容,
    // 模块大小不符不等于数据错位, 所以警告只在用户显式开启后才提示
    public volatile bool VersionCheckEnabled = false;

    // 预分配读缓冲 (避免每帧分配 ~2MB)
    private readonly byte[] _enemyBuf = new byte[Th06.ENEMY_COUNT * Th06.ENEMY_STRIDE];
    private readonly byte[] _bulletBuf = new byte[Th06.BULLET_COUNT * Th06.BULLET_STRIDE];
    private readonly byte[] _itemBuf = new byte[Th06.ITEM_COUNT * Th06.ITEM_STRIDE];
    private readonly byte[] _playerBuf = new byte[0xA28 + Th06.PLAYER_BULLET_COUNT * Th06.PLAYER_BULLET_STRIDE];
    private readonly byte[] _laserBuf = new byte[Th06.LASER_COUNT * Th06.LASER_STRIDE];
    private readonly byte[] _gmBuf = new byte[0x20];

    private readonly Th06Snapshot _snap = new();

    private int _hzCount;
    private long _hzWindowStart;

    public Th06Reader()
    {
        _thread = new Thread(ThreadLoop) { IsBackground = true, Name = "TH06Reader" };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(1500); } catch { }
        Detach();
    }

    public void RequestReattach()
    {
        Detach();
    }

    private void ThreadLoop()
    {
        var sw = Stopwatch.StartNew();
        while (_running)
        {
            long frameStart = sw.ElapsedMilliseconds;

            if (_hProcess == IntPtr.Zero)
            {
                TryAttach();
                if (_hProcess == IntPtr.Zero)
                {
                    Thread.Sleep(500);
                    continue;
                }
            }

            try
            {
                ReadFrame();
                lock (_sync)
                {
                    var dst = Latest;
                    CopySnapshot(_snap, dst);
                    dst.Valid = true;
                }
            }
            catch (Exception)
            {
                // 进程退出 / 读取失败 -> 重新附加
                Detach();
                continue;
            }

            // 统计实际采样率
            _hzCount++;
            long now = sw.ElapsedMilliseconds;
            if (now - _hzWindowStart >= 1000)
            {
                ActualHz = _hzCount * 1000.0 / (now - _hzWindowStart);
                _hzCount = 0;
                _hzWindowStart = now;
            }

            int interval = 1000 / Math.Max(10, PollHz);
            int elapsed = (int)(sw.ElapsedMilliseconds - frameStart);
            if (elapsed < interval) Thread.Sleep(interval - elapsed);
        }
    }

    // ---------------- 附加 ----------------

    private void TryAttach()
    {
        AttachStatus = "正在寻找 th06 进程…";
        VersionWarning = "";
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    // 只按进程名匹配 —— 绝不访问 MainModule:
                    // 枚举进程的模块列表在部分进程上极慢甚至永久阻塞,
                    // 曾导致"先开工具后开游戏"时扫描卡死、永远无法附加。
                    string name;
                    try { name = p.ProcessName; }
                    catch (Exception) { continue; } // 进程已退出

                    bool match = name.Contains("th06", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("紅魔郷") || name.Contains("红魔乡");
                    if (!match) continue;

                    IntPtr h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;

                    // 用 RPM 直接读 PE 头验证目标 (代替 MainModule), 顺便校验版本
                    if (!VerifyTh06Image(h))
                    {
                        CloseHandle(h);
                        continue;
                    }

                    _hProcess = h;
                    _pid = p.Id;
                    _procName = name + ".exe";
                    _baseAddr = Th06.DEFAULT_IMAGE_BASE;
                    AttachStatus = $"已连接: {_procName} (PID {_pid})";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AttachStatus = "查找进程出错: " + ex.Message;
            return;
        }
        AttachStatus = "等待游戏进程… (请启动 th06)";
    }

    private readonly byte[] _peBuf = new byte[0x1000];

    /// <summary>读 0x400000 处 PE 头: MZ/PE 校验确保附加目标确为可读映像 (版本识别兜底);
    /// 模块大小与 1.02h 的比对 (错位检测) 只在 VersionCheckEnabled 开启时提示。</summary>
    private bool VerifyTh06Image(IntPtr h)
    {
        int sizeOfImage = ReadSizeOfImage(h);
        if (sizeOfImage <= 0) return false;
        if (VersionCheckEnabled && Math.Abs(sizeOfImage - Th06.EXPECTED_MODULE_SIZE) > 0x8000)
            VersionWarning = $"模块大小 {sizeOfImage} 与 v1.02h (512000) 不符, 数据可能错位!";
        return true;
    }

    /// <summary>读 PE 头返回 SizeOfImage; 不是合法映像返回 0。只读不写状态。</summary>
    private int ReadSizeOfImage(IntPtr h)
    {
        if (!ReadProcessMemory(h, (IntPtr)Th06.DEFAULT_IMAGE_BASE, _peBuf, (IntPtr)_peBuf.Length, out IntPtr read))
            return 0;
        int got = (int)read.ToInt64();
        if (got < 0x60 || _peBuf[0] != (byte)'M' || _peBuf[1] != (byte)'Z') return 0;
        int lfanew = BitConverter.ToInt32(_peBuf, 0x3C);
        if (lfanew <= 0 || lfanew + 0x54 > got) return 0;
        if (_peBuf[lfanew] != (byte)'P' || _peBuf[lfanew + 1] != (byte)'E') return 0;
        return BitConverter.ToInt32(_peBuf, lfanew + 0x50);
    }

    /// <summary>已附加时按当前开关重新验证版本 (UI 切换"版本错位检测"时调用, 不必断开重连)。
    /// 用独立缓冲, 避免与读取线程的 TryAttach 并发使用 _peBuf。</summary>
    public void RecheckVersion()
    {
        VersionWarning = "";
        if (!VersionCheckEnabled || _hProcess == IntPtr.Zero) return;
        var buf = new byte[0x1000];
        if (!ReadProcessMemory(_hProcess, (IntPtr)Th06.DEFAULT_IMAGE_BASE, buf, (IntPtr)buf.Length, out IntPtr read))
            return;
        int got = (int)read.ToInt64();
        if (got < 0x60 || buf[0] != (byte)'M' || buf[1] != (byte)'Z') return;
        int lfanew = BitConverter.ToInt32(buf, 0x3C);
        if (lfanew <= 0 || lfanew + 0x54 > got) return;
        int sizeOfImage = BitConverter.ToInt32(buf, lfanew + 0x50);
        if (Math.Abs(sizeOfImage - Th06.EXPECTED_MODULE_SIZE) > 0x8000)
            VersionWarning = $"模块大小 {sizeOfImage} 与 v1.02h (512000) 不符, 数据可能错位!";
    }

    private void Detach()
    {
        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        _pid = -1;
        _procName = "";
        lock (_sync) Latest.Valid = false;
        AttachStatus = "未连接";
    }

    // ---------------- 读取 ----------------

    private bool Rpm(uint va, byte[] buf, int len)
    {
        IntPtr addr = (IntPtr)(_baseAddr + (va - Th06.DEFAULT_IMAGE_BASE));
        if (!ReadProcessMemory(_hProcess, addr, buf, (IntPtr)len, out IntPtr read) || read.ToInt64() != len)
            throw new IOException("ReadProcessMemory 失败");
        return true;
    }

    /// <summary>游戏区 ±REGION_MARGIN 判定; NaN 坐标一律视为界外 (被丢弃)。</summary>
    private static bool InField(float x, float y)
        => x >= -Th06.REGION_MARGIN && x <= Th06.REGION_W + Th06.REGION_MARGIN
        && y >= -Th06.REGION_MARGIN && y <= Th06.REGION_H + Th06.REGION_MARGIN;

    private void ReadFrame()
    {
        _snap.TimestampMs = Environment.TickCount64;

        // GameManager 头部 (难度 / 得分)
        Rpm(Th06.VA_GAME_MANAGER, _gmBuf, _gmBuf.Length);
        _snap.Difficulty = BitConverter.ToInt32(_gmBuf, Th06.GM_DIFFICULTY_OFF);
        _snap.Score = BitConverter.ToInt32(_gmBuf, Th06.GM_SCORE_OFF);

        // Player (含自机弹数组)
        Rpm(Th06.VA_PLAYER, _playerBuf, _playerBuf.Length);
        _snap.PlayerX = BitConverter.ToSingle(_playerBuf, Th06.PLAYER_POS_OFF);
        _snap.PlayerY = BitConverter.ToSingle(_playerBuf, Th06.PLAYER_POS_OFF + 4);
        _snap.PlayerHitR = BitConverter.ToSingle(_playerBuf, Th06.PLAYER_HITBOX_SIZE_OFF);
        _snap.PlayerState = (sbyte)_playerBuf[Th06.PLAYER_STATE_OFF];
        _snap.PlayerFocus = _playerBuf[Th06.PLAYER_ISFOCUS_OFF] != 0;

        int pb = 0;
        var pbs = MemoryMarshal.Cast<byte, RawPlayerBullet>(
            _playerBuf.AsSpan(Th06.PLAYER_BULLETS_OFF));
        for (int i = 0; i < pbs.Length && pb < _snap.PlayerBullets.Length; i++)
        {
            if (pbs[i].BulletState != 1) continue;
            float x = pbs[i].X, y = pbs[i].Y;
            if (!InField(x, y)) continue;
            _snap.PlayerBullets[pb++] = new PlayerBulletEnt { X = x, Y = y };
        }
        _snap.PlayerBulletCount = pb;

        // 敌弹
        Rpm(Th06.VA_BULLET_MANAGER + (uint)Th06.BULLET_MGR_BULLETS_OFF, _bulletBuf, _bulletBuf.Length);
        int nb = 0;
        var bs = MemoryMarshal.Cast<byte, RawBullet>(_bulletBuf);
        for (int i = 0; i < bs.Length; i++)
        {
            ushort st = bs[i].State;
            if (st == 0) continue;
            float x = bs[i].X, y = bs[i].Y;
            // 游戏区 (384x448) 边距以外不读; 边缘的出界部分由渲染层区域框精确裁掉
            if (!InField(x, y)) continue;
            _snap.Bullets[nb++] = new BulletEnt
            {
                X = x,
                Y = y,
                R = Math.Max(bs[i].GrazeX * 0.5f, 0.5f),          // 撞死半径 = grazeSize/2
                GrazeR = bs[i].GrazeX * 0.5f + 20.0f,             // 擦弹半径 = grazeSize/2 + 20
                State = st,
            };
        }
        _snap.BulletCount = nb;

        // 激光 (BulletManager.lasers[64])
        Rpm(Th06.VA_BULLET_MANAGER + (uint)Th06.BULLET_MGR_LASERS_OFF, _laserBuf, _laserBuf.Length);
        int nl = 0;
        var ls = MemoryMarshal.Cast<byte, RawLaser>(_laserBuf);
        for (int i = 0; i < ls.Length; i++)
        {
            ref readonly RawLaser l = ref ls[i];
            if (l.InUse == 0) continue;
            float fullLen = l.EndOffset - l.StartOffset;
            if (fullLen <= 0f) continue;
            float dirX = MathF.Cos(l.Angle), dirY = MathF.Sin(l.Angle);
            float midOff = l.StartOffset + fullLen * 0.5f;

            // 致死盒精确复算 (与 BulletManager::OnUpdate 一致, 含 ZUN 蓄力判定 bug)
            bool deadly;
            float deadlyHalfLen;
            if (l.State == 0)
            {
                if ((l.Flags & 1) != 0)
                {
                    deadlyHalfLen = fullLen / 2f;
                }
                else
                {
                    int res = Math.Min(l.StartTime, 30);
                    float growLen = (l.StartTime - res < l.Timer)
                        ? l.Timer * l.Width / Math.Max(l.StartTime, 1)
                        : 1.2f;
                    deadlyHalfLen = growLen / 4f;
                }
                deadly = l.Timer >= l.HitboxStartTime;
            }
            else if (l.State == 1)
            {
                deadlyHalfLen = fullLen / 2f;
                deadly = true;
            }
            else
            {
                float shrinkLen = l.Width - l.Timer * l.Width / Math.Max(l.DespawnDuration, 1);
                deadlyHalfLen = (l.Flags & 1) != 0 ? fullLen / 2f : shrinkLen / 4f;
                deadly = l.Timer < l.HitboxEndDelay;
            }

            float ax = l.PosX + dirX * l.StartOffset, ay = l.PosY + dirY * l.StartOffset;
            float bx = l.PosX + dirX * l.EndOffset, by = l.PosY + dirY * l.EndOffset;
            float cx = l.PosX + dirX * midOff, cy = l.PosY + dirY * midOff;
            // 三点全在区域边距外才丢弃 (光束常贯穿屏幕, 端点出界很正常)
            if (!InField(ax, ay) && !InField(bx, by) && !InField(cx, cy)) continue;

            _snap.Lasers[nl++] = new LaserEnt
            {
                X0 = ax,
                Y0 = ay,
                X1 = bx,
                Y1 = by,
                HalfW = l.Width / 4f,
                DeadlyCx = cx,
                DeadlyCy = cy,
                DeadlyHalfLen = deadlyHalfLen,
                Deadly = deadly,
                State = l.State,
            };
        }
        _snap.LaserCount = nl;

        // 敌人
        Rpm(Th06.VA_ENEMY_MANAGER + (uint)Th06.ENEMY_MGR_ITEMS_OFF, _enemyBuf, _enemyBuf.Length);
        int ne = 0;
        var es = MemoryMarshal.Cast<byte, RawEnemy>(_enemyBuf);
        for (int i = 0; i < es.Length - 1; i++) // 最后一个是 dummy 槽位
        {
            ref readonly RawEnemy e = ref es[i];
            // 死亡路径 (EnemyManager.cpp): deathMode=DespawnNoCallback(默认) -> isSlotOccupied=false;
            // 其余死亡 -> isInteractable=false。两条路都被下面条件挡住: 敌人死掉当帧即消失。
            if (!e.Occupied || !e.IsInteractable) continue;
            if (!InField(e.X, e.Y)) continue;
            _snap.Enemies[ne++] = new EnemyEnt
            {
                X = e.X,
                Y = e.Y,
                KillHalfW = e.HitboxDimX / 3.0f,   // 撞死盒全宽 = dim/1.5, 半宽 = dim/3
                KillHalfH = e.HitboxDimY / 3.0f,
                ShootHalfW = e.HitboxDimX / 2.0f,  // 可击打盒全宽 = dim, 半宽 = dim/2
                ShootHalfH = e.HitboxDimY / 2.0f,
                Life = e.Life,
                MaxLife = e.MaxLife,
                IsBoss = e.IsBoss,
                IsCollidable = e.IsCollidable,
                IsDamageable = e.IsDamageable,
                IsInvisible = e.IsInvisible,
            };
        }
        _snap.EnemyCount = ne;

        // 道具
        Rpm(Th06.VA_ITEM_MANAGER, _itemBuf, _itemBuf.Length);
        int ni = 0;
        var its = MemoryMarshal.Cast<byte, RawItem>(_itemBuf);
        for (int i = 0; i < its.Length - 1; i++)
        {
            if (its[i].IsInUse == 0) continue;
            float x = its[i].X, y = its[i].Y;
            if (!InField(x, y)) continue;
            _snap.Items[ni++] = new ItemEnt { X = x, Y = y, Type = its[i].ItemType };
        }
        _snap.ItemCount = ni;
    }

    private static void CopySnapshot(Th06Snapshot src, Th06Snapshot dst)
    {
        dst.TimestampMs = src.TimestampMs;
        dst.PlayerX = src.PlayerX; dst.PlayerY = src.PlayerY; dst.PlayerHitR = src.PlayerHitR;
        dst.PlayerState = src.PlayerState; dst.PlayerFocus = src.PlayerFocus;
        dst.Difficulty = src.Difficulty; dst.Score = src.Score;
        Array.Copy(src.Bullets, dst.Bullets, src.BulletCount); dst.BulletCount = src.BulletCount;
        Array.Copy(src.Enemies, dst.Enemies, src.EnemyCount); dst.EnemyCount = src.EnemyCount;
        Array.Copy(src.PlayerBullets, dst.PlayerBullets, src.PlayerBulletCount); dst.PlayerBulletCount = src.PlayerBulletCount;
        Array.Copy(src.Items, dst.Items, src.ItemCount); dst.ItemCount = src.ItemCount;
        Array.Copy(src.Lasers, dst.Lasers, src.LaserCount); dst.LaserCount = src.LaserCount;
    }
}
