using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Observed facts about the local process that serves a loopback inference endpoint.
/// Everything is best-effort: any failure yields nulls rather than exceptions.
/// </summary>
public sealed class LocalProcessInfo
{
    public int ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ProcessName { get; init; }
    public string? CommandLine { get; init; }
    public List<string> Modules { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

/// <summary>
/// Finds the process listening on a loopback TCP port and inspects it (binary path, loaded
/// modules, command line). Windows uses GetExtendedTcpTable; Linux reads /proc. Remote hosts
/// are never inspected.
/// </summary>
public static partial class LocalProcessInspector
{
    public static bool IsLoopback(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    public static LocalProcessInfo? Inspect(string serverUrl, TimeSpan? commandLineTimeout = null)
    {
        try
        {
            if (!IsLoopback(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var pid = FindListeningProcessId(uri.Port);
            if (pid is null or <= 0)
            {
                return null;
            }

            return Inspect(pid.Value, commandLineTimeout ?? TimeSpan.FromSeconds(8));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static LocalProcessInfo? Inspect(int pid, TimeSpan commandLineTimeout)
    {
        var notes = new List<string>();
        string? executable = null;
        string? processName = null;
        var modules = new List<string>();

        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
            try
            {
                executable = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                notes.Add("main module not readable: " + ex.Message);
            }

            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (!string.IsNullOrWhiteSpace(module.FileName))
                    {
                        modules.Add(module.FileName);
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                notes.Add("module list not readable: " + ex.Message);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            executable ??= TryReadLink($"/proc/{pid}/exe");
            if (modules.Count == 0)
            {
                modules.AddRange(ReadLinuxMappedLibraries(pid));
            }
        }

        var commandLine = ReadCommandLine(pid, commandLineTimeout, notes);
        return new LocalProcessInfo
        {
            ProcessId = pid,
            ExecutablePath = executable,
            ProcessName = processName,
            CommandLine = commandLine,
            Modules = modules,
            Notes = notes
        };
    }

    public static int? FindListeningProcessId(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            return FindListeningProcessIdWindows(port);
        }

        if (OperatingSystem.IsLinux())
        {
            return FindListeningProcessIdLinux(port);
        }

        return null;
    }

    // ---- Windows: iphlpapi GetExtendedTcpTable ---------------------------------------

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int MibTcpStateListen = 2;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    private static int? FindListeningProcessIdWindows(int port)
    {
        var fromV4 = ReadTcpTable(AfInet, port);
        if (fromV4.HasValue)
        {
            return fromV4;
        }

        return ReadTcpTable(AfInet6, port);
    }

    private static int? ReadTcpTable(int family, int port)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
            if (result != 0)
            {
                return null;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, 4);
            if (family == AfInet)
            {
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPointer, i * rowSize));
                    if (row.State == MibTcpStateListen && NetworkPort(row.LocalPort) == port)
                    {
                        return (int)row.OwningPid;
                    }
                }
            }
            else
            {
                var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                for (var i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(IntPtr.Add(rowPointer, i * rowSize));
                    if (row.State == MibTcpStateListen && NetworkPort(row.LocalPort) == port)
                    {
                        return (int)row.OwningPid;
                    }
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // The port is stored in network byte order inside the low 16 bits.
    private static int NetworkPort(uint rawPort) => (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));

    // ---- Linux: /proc/net/tcp + /proc/<pid>/fd ----------------------------------------

    private static int? FindListeningProcessIdLinux(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table))
            {
                continue;
            }

            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10)
                {
                    continue;
                }

                var local = parts[1];
                var state = parts[3];
                var colon = local.LastIndexOf(':');
                if (colon < 0 || state != "0A")
                {
                    continue;
                }

                if (int.TryParse(local[(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var localPort) && localPort == port)
                {
                    inodes.Add(parts[9]);
                }
            }
        }

        if (inodes.Count == 0)
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(directory, "fd")))
                {
                    var target = TryReadLink(fd);
                    if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal))
                    {
                        var inode = target[8..^1];
                        if (inodes.Contains(inode))
                        {
                            return pid;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Other users' processes are not readable; keep scanning.
            }
        }

        return null;
    }

    private static string? TryReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLinuxMappedLibraries(int pid)
    {
        var maps = $"/proc/{pid}/maps";
        if (!File.Exists(maps))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(maps))
        {
            var index = line.IndexOf('/');
            if (index < 0)
            {
                continue;
            }

            var path = line[index..].Trim();
            if ((path.Contains(".so", StringComparison.Ordinal)) && seen.Add(path))
            {
                yield return path;
            }
        }
    }

    // ---- Command line ------------------------------------------------------------------

    private static string? ReadCommandLine(int pid, TimeSpan timeout, List<string> notes)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var path = $"/proc/{pid}/cmdline";
                if (File.Exists(path))
                {
                    var raw = File.ReadAllText(path);
                    var arguments = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    return string.Join(' ', arguments.Select(QuoteIfNeeded));
                }

                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            // PowerShell/CIM is the supported way to read another process' command line on
            // Windows without undocumented PEB reads. Bounded by a timeout; failures are notes.
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Best effort.
                }

                notes.Add("command line lookup timed out");
                return null;
            }

            var text = output.Result.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            notes.Add("command line not readable: " + ex.Message);
            return null;
        }
    }

    private static string QuoteIfNeeded(string argument)
        => argument.Contains(' ') ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;

    /// <summary>Splits a command line into arguments honoring double quotes.</summary>
    public static IReadOnlyList<string> Tokenize(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>Redacts API keys and similar secrets from a command line before it is archived.</summary>
    public static string Redact(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        return SecretArgumentRegex().Replace(commandLine, "$1 <redacted>");
    }

    [GeneratedRegex(@"(--api-key(?:-file)?|--hf-token|--api-key=)\s*(?:""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretArgumentRegex();
}
