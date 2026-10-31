namespace OverlayUtils.UIAccess;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => App.AppHost.Run(args, App.LauncherKind.UIAccess);
}
