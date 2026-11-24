namespace TetherNest;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--broker" && int.TryParse(args[2], out int parent))
        {
            try { Broker.Run(args[1], parent); } catch { /* No UI in privileged mode; connection failure surfaces in the widget. */ }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
