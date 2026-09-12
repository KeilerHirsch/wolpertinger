using System.Diagnostics;

namespace Wolpertinger.Edge.Kernel;

public interface IChildProcessContainment
{
    void Assign(Process process);
}
