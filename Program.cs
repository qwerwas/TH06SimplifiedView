using System.Runtime.InteropServices;

namespace TH06SimplifiedView;

internal static class Program
{
    [DllImport("winmm.dll")]
    private static extern int timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern int timeEndPeriod(uint period);

    [STAThread]
    private static void Main()
    {
        // 系统定时器分辨率默认 ~15.6ms, 会把 Thread.Sleep 的精度拖到 30-40fps;
        // 提到 1ms, 限帧模式才能真正跑满目标帧率
        timeBeginPeriod(1);
        try
        {
            ApplicationConfiguration.Initialize();

            // 经典游戏主循环 (DoEvents 模式): 每轮先处理完积压消息, 再渲染一帧。
            // 不用 Application.Run + async 渲染循环: Task.Yield 每帧向消息队列自投递回调,
            // 高帧率下消息泵处理不过来, 窗口表现为"无响应"; DoEvents 循环同步顺序处理, 不存在该问题。
            var form = new MainForm();
            form.Show();
            while (!form.IsDisposed)
            {
                Application.DoEvents();

                // 最小化时窗口不可见, 降频省 CPU (恢复后立即回到正常帧率)
                if (form.WindowState == FormWindowState.Minimized)
                {
                    Thread.Sleep(50);
                    continue;
                }

                form.RenderFrame();
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }
}
