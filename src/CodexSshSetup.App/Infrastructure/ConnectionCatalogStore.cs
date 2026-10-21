using System.IO;
using System.Text.Json;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.App.Infrastructure;

public sealed class ConnectionCatalogStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ConnectionCatalogStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSshSetup");

    public async Task<ConnectionCatalog> LoadAsync(string sshConfig, CancellationToken token)
    {
        var path = Path.Combine(_directory, "connections.json");
        if (!File.Exists(path))
            return ConnectionHistory.Import(await new AppConfigStore(_directory).LoadAsync(token), sshConfig);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ConnectionCatalog>(stream, Options, token)
                ?? throw new InvalidDataException("节点列表为空，请检查 connections.json。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("节点列表文件损坏，原文件已保留。请查看日志后恢复备份。", exception);
        }
    }

    public async Task SaveAsync(ConnectionCatalog catalog, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var path = Path.Combine(_directory, "connections.json");
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, Options, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(tempPath, path, path + ".bak");
            else File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            _gate.Release();
        }
    }
}
