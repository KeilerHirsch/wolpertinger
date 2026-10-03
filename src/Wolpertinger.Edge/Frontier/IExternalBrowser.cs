using System.Diagnostics;

namespace Wolpertinger.Edge.Frontier;

public interface IExternalBrowser
{
    void Open(Uri uri);
}

public sealed class SystemExternalBrowser : IExternalBrowser
{
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }
}
