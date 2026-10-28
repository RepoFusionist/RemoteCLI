using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteCli.Local;

/// <summary>
/// Finds running Remote CLI service hosts independently of the MCP executable directory. The process list is only a
/// discovery hint; the MCP client also checks the actual named-pipe server PID after connecting.
/// </summary>
public sealed class DesktopProcessLocator
{
    public DesktopProcessLocator(string? executablePath)
    {
        // Kept for source compatibility. A release directory is not a service identity.
    }

    public IReadOnlySet<int> FindCandidateProcessIds()
    {
        var processIds = new HashSet<int>();
        if (!OperatingSystem.IsWindows())
        {
            return processIds;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("RemoteCli.Desktop");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return processIds;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    var actualPath = process.MainModule?.FileName;
                    if (IsMatchingDesktopProcess(string.Empty, actualPath, process.MainWindowHandle != IntPtr.Zero))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                {
                    // Inaccessible or exiting processes are not considered bindable hosts.
                }
            }
        }

        return processIds;
    }

    public static bool IsMatchingDesktopProcess(string expectedPath, string? actualPath, bool hasMainWindow)
    {
        if (!hasMainWindow || string.IsNullOrWhiteSpace(actualPath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFileName(actualPath), "RemoteCli.Desktop.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

internal static class NamedPipeProcessBinding
{
    public static bool IsConnectedToAny(SafePipeHandle pipeHandle, IReadOnlySet<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        ArgumentNullException.ThrowIfNull(processIds);
        if (!OperatingSystem.IsWindows() || !GetNamedPipeServerProcessId(pipeHandle, out var serverProcessId) || serverProcessId > int.MaxValue)
        {
            return false;
        }

        return processIds.Contains((int)serverProcessId);
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetNamedPipeServerProcessId")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipeHandle, out uint serverProcessId);
}
