using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;
using System.Collections.Concurrent;

namespace CodexSshSetup.Core.Tests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexSshSetupTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Process_runner_captures_real_stdout_stderr_and_exit_code()
    {
        var runner = new ProcessRunner();
        var lines = new ConcurrentQueue<string>();
        var progress = new InlineProgress<string>(lines.Enqueue);
        var command = new CommandSpec("cmd.exe", ["/d", "/c", "echo standard & echo problem 1>&2"]);

        var result = await runner.RunAsync(command, progress, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("standard", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("problem", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("standard", StringComparison.Ordinal));
    }

    [Fact]
    public void Interactive_process_uses_the_system_console_and_does_not_capture_password_input()
    {
        var command = new CommandSpec("ssh.exe", ["user@host", "true"], Interactive: true);

        var startInfo = ProcessRunner.CreateStartInfo(command);

        Assert.True(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.False(startInfo.CreateNoWindow);
    }

    [Fact]
    public void Batch_command_found_on_path_is_resolved_before_launch()
    {
        var expectedNpm = Environment.GetEnvironmentVariable("PATH")!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "npm.cmd"))
            .First(File.Exists);
        var command = new CommandSpec("npm.cmd", ["--version"]);

        var startInfo = ProcessRunner.CreateStartInfo(command);

        Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
        Assert.Equal(Path.GetFullPath(expectedNpm), startInfo.FileName, ignoreCase: true);
    }

    [Fact]
    public async Task Config_store_round_trips_through_an_atomic_json_file()
    {
        var store = new AppConfigStore(_root);
        var config = new AppConfig
        {
            Profile = new ServerProfile("host.example", "user", 22, "host"),
            LocalProxyPort = 7890
        };

        await store.SaveAsync(config, CancellationToken.None);
        var restored = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(config, restored);
        Assert.True(File.Exists(Path.Combine(_root, "config.json")));
        Assert.False(File.Exists(Path.Combine(_root, "config.json.tmp")));
    }

    [Fact]
    public async Task Malformed_config_does_not_crash_application_startup()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), "{ broken json");
        var store = new AppConfigStore(_root);

        var restored = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(new AppConfig(), restored);
        Assert.True(File.Exists(Path.Combine(_root, "config.json.invalid")));
    }

    [Fact]
    public async Task File_log_redacts_secrets_before_writing()
    {
        var log = new FileLog(Path.Combine(_root, "logs"));

        await log.WriteAsync("Authorization: Bearer top-secret\nConnection refused");
        var content = string.Join("\n", Directory.GetFiles(Path.Combine(_root, "logs")).Select(File.ReadAllText));

        Assert.DoesNotContain("top-secret", content, StringComparison.Ordinal);
        Assert.Contains("Connection refused", content, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
