using System.Text.Json;
using GptController.Models;

namespace GptController.Infrastructure;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new AppSettings();
        }

        try
        {
            AppSettings? settings;
            await using (var stream = File.OpenRead(_paths.SettingsFile))
            {
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            }
            if (settings is null)
            {
                return new AppSettings();
            }
            // Legacy 15-minute settings adopt the new default; other intervals remain.
            if (settings.RefreshPolicyVersion < 2)
            {
                settings = settings with
                {
                    QuotaRefreshMinutes = settings.QuotaRefreshMinutes == 15
                        ? AppSettings.DefaultRefreshMinutes
                        : settings.QuotaRefreshMinutes,
                    RefreshPolicyVersion = 2
                };
                try
                {
                    await SaveAsync(settings, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Retain loaded preferences even if migration cannot be persisted yet.
                }
            }
            return settings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(_paths.SettingsFile, bytes, cancellationToken);
    }
}
