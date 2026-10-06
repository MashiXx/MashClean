using System.Diagnostics;
using System.Text;

namespace CleanBoost.Core;

/// <summary>Kết quả chạy một tiến trình con.</summary>
public sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Chạy chương trình với đường dẫn tuyệt đối và mảng tham số cố định — không bao giờ qua <c>cmd /c</c> hay shell
/// (SYSTEM_DESIGN mục 9.3).
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessOutput> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default, string? workingDirectory = null)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("Executable must be an absolute path", nameof(executable));
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }
        string o, e;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e = stderr.ToString();
        return new ProcessOutput(timedOut ? -1 : process.ExitCode, o, e, timedOut);
    }

    /// <summary>Đường dẫn tuyệt đối của chương trình hệ thống trong <c>System32</c> (vd <c>ipconfig.exe</c>).</summary>
    public static string System32(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
}
