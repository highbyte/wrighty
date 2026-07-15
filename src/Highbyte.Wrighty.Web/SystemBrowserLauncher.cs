using System.Diagnostics;

namespace Highbyte.Wrighty.Web;

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
