using System.Runtime.InteropServices;

namespace TH06SimplifiedView;

/// <summary>
/// th06 (东方红魔乡, v1.02h, 2002 原版) 内存布局。
/// 地址来源: happyhavoc/th06 反编译工程 config/globals.csv
/// 结构体偏移: 由 src/*.hpp 逐字段推导, 与 ZUN_ASSERT_TYPE 断言全部吻合。
/// 注意: 仅适用于真原版 1.02h exe (512000 字节), 不适用于 Steam 新典 (th06nc)。
/// </summary>
internal static class Th06
{
    // ---- 全局变量虚拟地址 (imagebase 0x400000, 无 ASLR) ----
    public const uint VA_ENEMY_MANAGER   = 0x004B79C8;
    public const uint VA_BULLET_MANAGER  = 0x005A5FF8;
    public const uint VA_ITEM_MANAGER    = 0x0069E268;
    public const uint VA_GAME_MANAGER    = 0x0069BCA0;
    public const uint VA_PLAYER          = 0x006CA628;

    public const uint DEFAULT_IMAGE_BASE = 0x00400000;
    public const uint EXPECTED_MODULE_SIZE = 512_000; // v1.02h 字节数

    // ---- 游戏区 (GameManager.hpp 常量) ----
    public const float REGION_W = 384.0f;
    public const float REGION_H = 448.0f;
    public const float REGION_LEFT = 32.0f;   // 仅渲染时游戏内部使用, 实体坐标已是局部坐标
    public const float REGION_TOP = 16.0f;
    // 读取层过滤边距: 中心在区域外 40 以内仍保留 (渲染层 SetClip 精确裁到区域框, 与原版画面一致),
    // 更远的一律不读
    public const float REGION_MARGIN = 40f;

    // ---- EnemyManager 布局 ----
    public const int ENEMY_MGR_ITEMS_OFF = 0x0ED0;     // Enemy enemies[257]
    public const int ENEMY_COUNT = 257;                // 含 1 个 dummy 槽位 (最后一个是)
    public const int ENEMY_STRIDE = 0x0EC8;

    // ---- BulletManager 布局 ----
    public const int BULLET_MGR_BULLETS_OFF = 0x5600;  // 16 * BulletTypeSprites(0x560)
    public const int BULLET_COUNT = 640;
    public const int BULLET_STRIDE = 0x5C4;
    // 激光数组: EoSD 没有独立 LaserManager, 激光存放在 BulletManager.lasers[64]
    // 0x5600 + 640*0x5C4 = 0xEC000; 其后 64*0x270 + 24(尾部字段) = 0xF5C18,
    // 与 ZUN_ASSERT_TYPE(BulletManager, 0xf5c18) 吻合
    public const int BULLET_MGR_LASERS_OFF = 0xEC000;
    public const int LASER_COUNT = 64;                 // MAX_ENEMY_LASERS
    public const int LASER_STRIDE = 0x270;

    // ---- ItemManager 布局 ----
    public const int ITEM_COUNT = 513;                 // 含 1 个 dummy 槽位
    public const int ITEM_STRIDE = 0x144;

    // ---- Player 布局 ----
    public const int PLAYER_POS_OFF = 0x440;           // positionCenter (D3DXVECTOR3)
    public const int PLAYER_HITBOX_SIZE_OFF = 0x488;   // hitboxSize (D3DXVECTOR3)
    public const int PLAYER_STATE_OFF = 0x9E0;         // PlayerState
    public const int PLAYER_ISFOCUS_OFF = 0x9E3;
    public const int PLAYER_BULLETS_OFF = 0xA28;       // PlayerBullet bullets[80]
    public const int PLAYER_BULLET_COUNT = 80;
    public const int PLAYER_BULLET_STRIDE = 0x158;

    // ---- GameManager 布局 (只读头部) ----
    public const int GM_SCORE_OFF = 0x4;
    public const int GM_DIFFICULTY_OFF = 0x10;
}

/// <summary>敌弹 (Bullet, 0x5C4)。state: 0=空 1=飞行中(有判定) 2/3/4=生成动画(无判定) 5=消散中。
/// 死亡判定: BulletManager.cpp 调用 CalcKillBoxCollision(pos, grazeSize), 盒= pos±grazeSize/2
///  => 撞死半径 = GrazeX/2; 擦弹半径 = GrazeX/2+20 (CheckGraze 内部 +20)。</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x5C4)]
public struct RawBullet
{
    [FieldOffset(0x550)] public float GrazeX;      // grazeSize.x
    [FieldOffset(0x55D)] public byte BulletHeight; // (非死亡判定用, 死亡判定走 grazeSize)
    [FieldOffset(0x560)] public float X;
    [FieldOffset(0x564)] public float Y;
    [FieldOffset(0x5BE)] public ushort State;
}

/// <summary>敌人 (Enemy, 0xEC8)。
/// hitboxDimensions 为"全宽"语义 (SetVecCorners: pos ± size/2):
///   可击打盒全宽 = dim => 半宽 = dim/2 (CalcDamageToEnemy)
///   撞死盒全宽 = dim/1.5 => 半宽 = dim/3 (EnemyManager.cpp: hitboxDimensions/1.5 + CalcKillBoxCollision/2)</summary>
[StructLayout(LayoutKind.Explicit, Size = 0xEC8)]
public struct RawEnemy
{
    [FieldOffset(0xC6C)] public float X;           // position
    [FieldOffset(0xC70)] public float Y;
    [FieldOffset(0xC78)] public float HitboxDimX;  // hitboxDimensions.x (全宽)
    [FieldOffset(0xC7C)] public float HitboxDimY;  // hitboxDimensions.y (全宽)
    [FieldOffset(0xCE4)] public int Life;
    [FieldOffset(0xCE8)] public int MaxLife;
    [FieldOffset(0xE40)] public byte BossId;
    // EnemyFlags 位域 (src/Enemy.hpp; MSVC 位域从每字节 LSB 开始分配):
    //   byte0(E50): [0:1]movementMode [2:4]moveInterpMode [5]shootingDisabled [6]mirrored [7]isSlotOccupied
    //   byte1(E51): [0]isInteractable [1]isCollidable [2]hasBeenInBounds [3]isBoss [4]isDamageable [5:7]deathMode
    //   byte2(E52): [0]shouldClampPos [1]rotateAnm [2]disableCallStack [3]isInvisible [4]isTimeoutSpell
    // 注意: isSlotOccupied 是 byte0 的 bit7 (0x80)! 取 bit0 会拿到 movementMode 的奇偶,
    // 导致走 DespawnNoCallback 死亡 (只清槽位不清 isInteractable) 的敌人变成永不消失的幽灵。
    [FieldOffset(0xE50)] public byte Flags0;
    [FieldOffset(0xE51)] public byte Flags1;
    [FieldOffset(0xE52)] public byte Flags2;

    public readonly bool Occupied => (Flags0 & 0x80) != 0;
    public readonly bool IsInteractable => (Flags1 & 0x01) != 0;
    public readonly bool IsCollidable => (Flags1 & 0x02) != 0;
    public readonly bool IsBoss => (Flags1 & 0x08) != 0;
    public readonly bool IsDamageable => (Flags1 & 0x10) != 0;   // false = 无敌演出 (EnemyDeath_SetHpTo1 后剩 1 血退场)
    public readonly bool IsInvisible => (Flags2 & 0x08) != 0;
}

/// <summary>自机弹 (PlayerBullet, 0x158)。bulletState: 0=空 1=飞行 2=命中。</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x158)]
public struct RawPlayerBullet
{
    [FieldOffset(0x110)] public float X;   // position
    [FieldOffset(0x114)] public float Y;
    [FieldOffset(0x14E)] public short BulletState;
}

/// <summary>道具 (Item, 0x144)。</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x144)]
public struct RawItem
{
    [FieldOffset(0x110)] public float X;   // currentPosition
    [FieldOffset(0x114)] public float Y;
    [FieldOffset(0x140)] public sbyte ItemType;
    [FieldOffset(0x141)] public sbyte IsInUse;
}

/// <summary>敌激光 (Laser, 0x270, 位于 BulletManager.lasers[64])。
/// 几何: 光束从 pos 出发沿 angle 方向, 可见段 = [pos+dir*startOffset, pos+dir*endOffset], 视觉宽度 = width。
/// 判定 (Player.CalcLaserHitbox): 旋转 AABB, 盒 = laserCenter ± laserSize/2, laserSize=(沿束长, width/2)
///   => 垂直致死半宽恒为 width/4 (视觉宽度的一半!); 沿束半长按状态机:
///   state0 蓄力: flags&1 时 = fullLen/2, 否则 = 生长宽度/4 (ZUN bug: 判定是光束中点的小方块而非整条线)
///   state1 喷射: fullLen/2 (恰好覆盖可见段); state2 消散: 收缩宽度/4。
/// state: 0=蓄力(细线,无判定直到 hitboxStartTime) 1=喷射(全判定) 2=消散(timer<hitboxEndDelay 内仍有判定)。</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x270)]
public struct RawLaser
{
    [FieldOffset(0x220)] public float PosX;        // pos (光束起点)
    [FieldOffset(0x224)] public float PosY;
    [FieldOffset(0x22C)] public float Angle;
    [FieldOffset(0x230)] public float StartOffset;
    [FieldOffset(0x234)] public float EndOffset;
    [FieldOffset(0x238)] public float StartLength;
    [FieldOffset(0x23C)] public float Width;
    [FieldOffset(0x240)] public float Speed;
    [FieldOffset(0x244)] public int StartTime;     // 蓄力时长
    [FieldOffset(0x248)] public int HitboxStartTime;
    [FieldOffset(0x24C)] public int Duration;
    [FieldOffset(0x250)] public int DespawnDuration;
    [FieldOffset(0x254)] public int HitboxEndDelay;
    [FieldOffset(0x258)] public byte InUse;        // ZunBool
    [FieldOffset(0x25C)] public int Timer;         // ZunTimer.current
    [FieldOffset(0x268)] public ushort Flags;      // bit0 = 无蓄力动画 (整条束即时出现)
    [FieldOffset(0x26C)] public byte State;
}

// ---------------- 供渲染用的快照实体 ----------------

public struct BulletEnt
{
    public float X, Y, R, GrazeR;
    public int State;
}

public struct EnemyEnt
{
    public float X, Y;
    public float KillHalfW, KillHalfH;      // 撞死判定半宽/半高 = hitboxDimensions/3
    public float ShootHalfW, ShootHalfH;    // 可击打判定半宽/半高 = hitboxDimensions/2
    public int Life, MaxLife;
    public bool IsBoss, IsCollidable, IsInvisible, IsDamageable;
}

public struct PlayerBulletEnt
{
    public float X, Y;
}

public struct ItemEnt
{
    public float X, Y;
    public int Type;
}

public struct LaserEnt
{
    public float X0, Y0;            // 可见段近端 (pos + dir*startOffset)
    public float X1, Y1;            // 可见段远端 (pos + dir*endOffset)
    public float HalfW;             // 致死垂直半宽 = width/4
    public float DeadlyCx, DeadlyCy; // 致死盒中心 (光束中点)
    public float DeadlyHalfLen;     // 致死盒沿束半长
    public bool Deadly;             // 本帧是否带致死判定
    public int State;               // 0 蓄力 1 喷射 2 消散
}

public sealed class Th06Snapshot
{
    public bool Valid;
    public long TimestampMs;

    public float PlayerX, PlayerY, PlayerHitR;
    public int PlayerState;   // 0=存活 1=重生 2=死亡 3=无敌
    public bool PlayerFocus;

    public int Difficulty;    // 0 Easy .. 4 Extra
    public int Score;

    public readonly BulletEnt[] Bullets = new BulletEnt[Th06.BULLET_COUNT];
    public int BulletCount;

    public readonly EnemyEnt[] Enemies = new EnemyEnt[Th06.ENEMY_COUNT];
    public int EnemyCount;

    public readonly PlayerBulletEnt[] PlayerBullets = new PlayerBulletEnt[Th06.PLAYER_BULLET_COUNT];
    public int PlayerBulletCount;

    public readonly ItemEnt[] Items = new ItemEnt[Th06.ITEM_COUNT];
    public int ItemCount;

    public readonly LaserEnt[] Lasers = new LaserEnt[Th06.LASER_COUNT];
    public int LaserCount;
}
