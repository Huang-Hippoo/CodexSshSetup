using CodexSshSetup.Core.Logging;
using System.IO;

namespace CodexSshSetup.App.Infrastructure;

public sealed class FileLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 7;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentPath;

    public FileLog(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexSshSetup", "logs");
    }

    public string DirectoryPath => _directory;

    public async Task WriteAsync(string message)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            if (_currentPath is null ||
                (File.Exists(_currentPath) && new FileInfo(_currentPath).Length >= MaxBytes))
            {
                _currentPath = Path.Combine(_directory,
                    $"codex-ssh-setup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            }

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {SecretRedactor.Redact(message)}{Environment.NewLine}";
            await File.AppendAllTextAsync(_currentPath, line);
            Rotate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Rotate()
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("codex-ssh-setup-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaxFiles);
        foreach (var file in files)
        {
            file.Delete();
        }
    }
}
