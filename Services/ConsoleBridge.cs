using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostLock.Services;

/// <summary>
/// Attaches terminal output for console-oriented command-line options.
/// </summary>
internal static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    private static bool _attached;
    private static bool _tried;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static void WriteLine(string? text)
    {
        if (text is null) return;

        try
        {
            if (!_tried)
            {
                _tried = true;
                _attached = Attach();

                if (_attached)
                {
                    var stdout = Console.OpenStandardOutput();
                    if (!ReferenceEquals(stdout, Stream.Null))
                        Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                    else
                        _attached = false;
                }
            }

            if (!_attached) return;

            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch
        {
            // No console to write to; the command still ran.
        }
    }

    private static bool Attach()
    {
        try { return AttachConsole(AttachParentProcess); }
        catch { return false; }
    }
}
