using System.Diagnostics;

namespace DiskCleaner.Models;

/// <summary>
/// One snapshot of the running process names, so hundreds of items do not each enumerate every
/// process on the machine.
/// </summary>
public sealed class RunningProcesses
{
    private readonly HashSet<string> _names;

    private RunningProcesses(HashSet<string> names) => _names = names;

    public static RunningProcesses Capture()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Without a process list the warning badge simply never appears.
        }

        return new RunningProcesses(names);
    }

    public bool IsRunning(string[] names)
    {
        foreach (var name in names)
        {
            if (_names.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}
