namespace BtBattery;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--scan", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            var list = new BatteryService().ScanAsync().GetAwaiter().GetResult();
            Console.WriteLine();
            foreach (var d in list.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name))
                Console.WriteLine($"{d.Name,-28} {d.Address}  {(d.IsLowEnergy ? "LE     " : "Classic")}  " +
                                  $"{(d.IsConnected ? "connected   " : "disconnected")}  {d.Kind,-10}  " +
                                  $"battery={(d.Battery?.ToString() ?? "-")}");
            return;
        }

        using var mutex = new Mutex(true, "BluetoothBatteryWidget.SingleInstance", out bool first);
        if (!first) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
