using System.Text.Json;
using System.IO;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.App.Infrastructure;

public sealed class AppConfigStore
{
    private readonly string _directory;
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppConfigStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexSshSetup");
        _path = Path.Combine(_directory, "config.json");
    }

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new AppConfig();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken)
                ?? new AppConfig();
        }
        catch (JsonException)
        {
            File.Move(_path, _path + ".invalid", overwrite: true);
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var tempPath = _path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(tempPath, _path, overwrite: true);
    }
}
