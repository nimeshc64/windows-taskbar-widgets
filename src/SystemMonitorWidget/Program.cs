using BtBattery;

namespace SysMon;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--sample", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            var s = new SystemSampler();
            s.Sample();
            Console.WriteLine();
            Console.WriteLine($"CPU: {s.CpuName} ({s.LogicalProcessors} logical)");
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1000);
                s.Sample();
                Console.WriteLine($"cpu {s.Cpu,5:0.0}%  mem {s.MemoryLoad,5:0.0}% ({SystemSampler.Gb(s.MemoryUsed)}/{SystemSampler.Gb(s.MemoryTotal)})  " +
                                  $"down {SystemSampler.Speed(s.Down, false),10}  up {SystemSampler.Speed(s.Up, false),10}  " +
                                  $"{s.AdapterKind} '{s.AdapterName}' {s.LocalIp}  internet={s.Internet}");
            }
            return;
        }

        using var mutex = new Mutex(true, "SystemMonitorWidget.SingleInstance", out bool first);
        if (!first) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => SmLog.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => SmLog.Write("Fatal: " + e.ExceptionObject);

        ApplicationConfiguration.Initialize();
        Application.Run(new SysTrayApp());
    }
}
