using AudioCtl.Interop;
using BtBattery;

namespace AudioCtl;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using var svc = new SoundService();
            Console.WriteLine();
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            {
                var ep = svc[flow];
                Console.WriteLine($"{(flow == EDataFlow.Render ? "OUTPUT" : "INPUT")}  default: {ep.DeviceName}  " +
                                  $"volume {ep.Percent}%{(ep.Muted ? " (muted)" : "")}");
                foreach (var d in svc.GetDevices(flow))
                    Console.WriteLine($"   {(d.IsDefault ? "*" : " ")} {d.Name} — {d.Interface}  [formfactor {d.FormFactor}]");
            }
            return;
        }

        using var mutex = new Mutex(true, "AudioControlWidget.SingleInstance", out bool first);
        if (!first) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => SLog.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => SLog.Write("Fatal: " + e.ExceptionObject);

        ApplicationConfiguration.Initialize();
        Application.Run(new SoundTrayApp());
    }
}
