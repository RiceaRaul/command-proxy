using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cpx;

static class Runner
{
    public const int NotFound = 127;

    /// <summary>The child inherits stdin, stdout and stderr: streaming, TTY and interactive use work unchanged.</summary>
    public static int Passthrough(string command, IReadOnlyList<string> args)
    {
        using var process = Start(command, args, captureStdout: false, captureStderr: false);
        if (process is null) return NotFound;
        using var signals = new SignalForwarder(process);
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// Captures stdout into <paramref name="output"/>. stderr goes straight to the console unless
    /// <paramref name="mergeStderr"/> is set, in which case it is appended after stdout.
    /// Returns null when the command could not be started.
    /// </summary>
    public static int? Capture(string command, IReadOnlyList<string> args, bool mergeStderr, ArrayBufferWriter<byte> output)
    {
        using var process = Start(command, args, captureStdout: true, captureStderr: mergeStderr);
        if (process is null) return null;
        using var signals = new SignalForwarder(process);

        // Both pipes must be drained concurrently or a full stderr pipe blocks the child.
        Task<ArrayBufferWriter<byte>>? stderr = mergeStderr
            ? Task.Run(() =>
            {
                var buffer = new ArrayBufferWriter<byte>(4096);
                ReadAll(process.StandardError.BaseStream, buffer);
                return buffer;
            })
            : null;

        ReadAll(process.StandardOutput.BaseStream, output);
        if (stderr is not null) output.Write(stderr.GetAwaiter().GetResult().WrittenSpan);

        process.WaitForExit();
        return process.ExitCode;
    }

    static void ReadAll(Stream stream, ArrayBufferWriter<byte> buffer)
    {
        int read;
        while ((read = stream.Read(buffer.GetSpan(16 * 1024))) > 0) buffer.Advance(read);
    }

    static Process? Start(string command, IReadOnlyList<string> args, bool captureStdout, bool captureStderr)
    {
        var info = new ProcessStartInfo(ResolveCommand(command))
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureStdout,
            RedirectStandardError = captureStderr,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            return Process.Start(info);
        }
        catch (Win32Exception e)
        {
            // ENOENT / ERROR_FILE_NOT_FOUND and EACCES / ERROR_ACCESS_DENIED; the framework message is not readable here.
            var reason = e.NativeErrorCode switch
            {
                2 => "command not found",
                5 or 13 => "permission denied",
                _ => e.Message,
            };
            Console.Error.WriteLine($"cpx: {command}: {reason}");
            return null;
        }
    }

    /// <summary>
    /// On Windows npm, ng and tsc are .cmd shims, which process creation does not find by bare name.
    /// Resolve through PATH and PATHEXT to a full path; elsewhere the OS does the lookup.
    /// </summary>
    static string ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command) || command.AsSpan().ContainsAny('\\', '/'))
            return command;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate)) return candidate;
            }
        return command;
    }

    /// <summary>
    /// Ctrl+C reaches the child directly (same process group); cpx ignores it and waits, so the
    /// child's partial output and exit code still come through. SIGTERM is turned into killing the child tree.
    /// </summary>
    sealed class SignalForwarder : IDisposable
    {
        readonly PosixSignalRegistration _interrupt;
        readonly PosixSignalRegistration _terminate;

        public SignalForwarder(Process process)
        {
            _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, static context => context.Cancel = true);
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already exited
                }
            });
        }

        public void Dispose()
        {
            _interrupt.Dispose();
            _terminate.Dispose();
        }
    }
}
