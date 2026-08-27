using System.Diagnostics;
using System.IO;
using System.Text;
using CodexSshSetup.Core.Commands;

namespace CodexSshSetup.App.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        CommandSpec command,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        CommandSpec command,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(command);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        if (!command.Interactive)
        {
            process.OutputDataReceived += (_, args) => Append(args.Data, stdout, progress);
            process.ErrorDataReceived += (_, args) => Append(args.Data, stderr, progress);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {command.FileName}。");
        }

        if (!command.Interactive)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        if (command.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(command.StandardInput);
            process.StandardInput.Close();
        }

        using var timeoutCts = command.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            if (!command.Interactive)
            {
                process.WaitForExit();
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static ProcessStartInfo CreateStartInfo(CommandSpec command)
    {
        if (command.Interactive && command.StandardInput is not null)
        {
            throw new ArgumentException("交互进程不能同时重定向标准输入。", nameof(command));
        }

        var startInfo = new ProcessStartInfo(ResolveBatchFile(command.FileName))
        {
            UseShellExecute = command.Interactive,
            CreateNoWindow = !command.Interactive,
            RedirectStandardOutput = !command.Interactive,
            RedirectStandardError = !command.Interactive,
            RedirectStandardInput = !command.Interactive && command.StandardInput is not null
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string ResolveBatchFile(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName) ||
            !string.Equals(Path.GetExtension(fileName), ".cmd", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(fileName), ".bat", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return fileName;
        }

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return fileName;
    }

    private static void Append(string? line, StringBuilder target, IProgress<string>? progress)
    {
        if (line is null)
        {
            return;
        }

        lock (target)
        {
            target.AppendLine(line);
        }
        progress?.Report(line);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
