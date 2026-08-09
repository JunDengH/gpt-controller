using GptController.Models;
using GptController.Services;

namespace GptController.Tests;

public sealed class QwenCodexConfigServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GptController.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QwenAndDeepSeekShareOneOriginalBackupAndRestoreExactly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "config.toml");
        var catalogPath = Path.Combine(_root, "models.json");
        var statePath = Path.Combine(_root, "config-state.json");
        var helperPath = Path.Combine(_root, "helper.exe");
        await File.WriteAllTextAsync(
            configPath,
            "model = \"gpt-original\"\r\nmodel_provider = \"openai\"\r\n[projects.'D:\\\\work']\r\ntrust_level = \"trusted\"\r\n");
        await File.WriteAllBytesAsync(helperPath, []);
        var service = new DeepSeekCodexConfigService(new(
            configPath,
            catalogPath,
            statePath,
            helperPath));
        var qwen = new QwenConnection
        {
            Model = "qwen3-coder-plus",
            Region = QwenRegion.Virginia,
            Models =
            [
                new ApiModelDescriptor { Id = "qwen3-coder-plus" },
                new ApiModelDescriptor { Id = "qwen3.8-max" }
            ]
        };

        var applied = await service.ApplyAsync(ApiProviderDefinitions.ForQwen(qwen));
        var firstBackup = applied.BackupFilePath;
        var qwenConfig = await File.ReadAllTextAsync(configPath);
        Assert.Contains("model_provider = \"gpt_controller_qwen\"", qwenConfig);
        Assert.Contains("base_url = \"https://dashscope-us.aliyuncs.com/compatible-mode/v1/\"", qwenConfig);
        Assert.Contains("args = [\"get-token\", \"--provider\", \"qwen\"]", qwenConfig);
        Assert.DoesNotContain("experimental_bearer_token", qwenConfig);
        var catalog = await File.ReadAllTextAsync(catalogPath);
        Assert.Contains("qwen3-coder-plus", catalog);
        Assert.Contains("\"supports_search_tool\": false", catalog);

        var switched = await service.ChangeProviderAsync(
            ApiProviderDefinitions.ForDeepSeek(DeepSeekDefaults.ProModel));
        Assert.Equal(firstBackup, switched.BackupFilePath);
        var deepSeekConfig = await File.ReadAllTextAsync(configPath);
        Assert.Contains("model_provider = \"gpt_controller_deepseek\"", deepSeekConfig);
        Assert.DoesNotContain("[model_providers.gpt_controller_qwen]", deepSeekConfig);

        var restored = await service.RestoreAsync();
        Assert.Equal(DeepSeekConfigChangeStatus.Restored, restored.Status);
        var final = await File.ReadAllTextAsync(configPath);
        Assert.Contains("model = \"gpt-original\"", final);
        Assert.Contains("trust_level = \"trusted\"", final);
        Assert.DoesNotContain("gpt_controller_qwen", final);
        Assert.DoesNotContain("gpt_controller_deepseek", final);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
