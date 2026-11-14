using System.Diagnostics;

namespace FrameProof.Core.Util;

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class Shell
{
    public static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static ShellResult Run(string file, string args, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new ShellResult(-1, "", "zaman aşımı");
            }
            p.WaitForExit();
            return new ShellResult(p.ExitCode, o.Result, e.Result);
        }
        catch (Exception ex)
        {
            return new ShellResult(-2, "", ex.Message);
        }
    }

    public static async Task<ShellResult> RunAsync(string file, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        return new ShellResult(p.ExitCode, await o, await e);
    }
}
