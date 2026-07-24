using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using GptAccountManager.Infrastructure;

namespace GptAccountManager.Services;

public sealed record LoginStartResult(string LoginId, string AuthUrl);

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly RedactingLogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _notifications = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private long _requestId;
    private bool _disposed;

    private CodexAppServerClient(
        Process process,
        StreamWriter input,
        RedactingLogger logger)
    {
        _process = process;
        _input = input;
        _logger = logger;
        _stdoutPump = PumpStdoutAsync();
        _stderrPump = PumpStderrAsync();
    }

    public static async Task<CodexAppServerClient> StartAsync(
        string codexExecutable,
        string codexHome,
        RedactingLogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(codexExecutable))
        {
            throw new FileNotFoundException("codex.exe was not found.", codexExecutable);
        }

        Directory.CreateDirectory(codexHome);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = codexExecutable,
                Arguments = "app-server --listen stdio://",
                WorkingDirectory = codexHome,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.Environment["CODEX_HOME"] = codexHome;
        process.StartInfo.Environment["NO_COLOR"] = "1";

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start codex app-server.");
        }

        var client = new CodexAppServerClient(process, process.StandardInput, logger);
        try
        {
            await client.InitializeAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<AccountReadMetadata> ReadAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "account/read",
            new { refreshToken = false },
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (!result.TryGetProperty("account", out var account) ||
            account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException("The app-server did not return a signed-in account.");
        }

        return new AccountReadMetadata(
            ReadString(account, "email"),
            ReadString(account, "planType"),
            ReadString(account, "accountId") ?? ReadString(result, "accountId"));
    }

    public Task<JsonElement> ReadRateLimitsAsync(
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            "account/rateLimits/read",
            null,
            TimeSpan.FromSeconds(20),
            cancellationToken);

    public async Task<LoginStartResult> StartChatGptLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(
            "account/login/start",
            new
            {
                type = "chatgpt",
                useHostedLoginSuccessPage = true,
                appBrand = "chatgpt"
            },
            TimeSpan.FromSeconds(20),
            cancellationToken);

        var loginId = ReadString(result, "loginId")
            ?? throw new InvalidDataException("The login response did not include loginId.");
        var authUrl = ReadString(result, "authUrl")
            ?? throw new InvalidDataException("The login response did not include authUrl.");
        return new LoginStartResult(loginId, authUrl);
    }

    public async Task WaitForLoginCompletedAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        timeoutCts.CancelAfter(timeout);

        while (await _notifications.Reader.WaitToReadAsync(timeoutCts.Token))
        {
            while (_notifications.Reader.TryRead(out var notification))
            {
                if (ReadString(notification, "method") != "account/login/completed" ||
                    !notification.TryGetProperty("params", out var parameters))
                {
                    continue;
                }

                if (!string.Equals(
                        ReadString(parameters, "loginId"),
                        loginId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var success = parameters.TryGetProperty("success", out var successElement) &&
                              successElement.ValueKind == JsonValueKind.True;
                if (success)
                {
                    return;
                }

                throw new InvalidOperationException(
                    ReadString(parameters, "error") ?? "ChatGPT login did not complete.");
            }
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not register app-server request.");
        }

        try
        {
            await WriteMessageAsync(
                parameters is null
                    ? new Dictionary<string, object?>
                    {
                        ["id"] = id,
                        ["method"] = method
                    }
                    : new Dictionary<string, object?>
                    {
                        ["id"] = id,
                        ["method"] = method,
                        ["params"] = parameters
                    },
                cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            timeoutCts.CancelAfter(timeout);
            return await completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"app-server request timed out: {method}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "gpt-account-manager",
                    title = "GPT Account Manager",
                    version = "1.1.1"
                },
                capabilities = new
                {
                    experimentalApi = false,
                    requestAttestation = false,
                    optOutNotificationMethods = Array.Empty<string>()
                }
            },
            TimeSpan.FromSeconds(15),
            cancellationToken);
        await WriteMessageAsync(
            new Dictionary<string, object?>
            {
                ["method"] = "initialized",
                ["params"] = new { }
            },
            cancellationToken);
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _input.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_lifetime.Token);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch
                {
                    continue;
                }

                if (TryReadId(message, out var id) &&
                    _pending.TryGetValue(id, out var completion))
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        completion.TrySetException(new InvalidOperationException(
                            ReadString(error, "message") ?? "Unknown app-server error."));
                    }
                    else if (message.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        completion.TrySetException(
                            new InvalidDataException("app-server response has no result."));
                    }

                    continue;
                }

                if (message.TryGetProperty("method", out _))
                {
                    await _notifications.Writer.WriteAsync(message, _lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("app-server.stdout", exception);
        }
        finally
        {
            var failure = new InvalidOperationException("codex app-server exited.");
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(failure);
            }

            _notifications.Writer.TryComplete();
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_lifetime.Token);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    await _logger.WarningAsync("app-server.stderr", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // stderr diagnostics are best-effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            _input.Close();
        }
        catch
        {
            // Ignore closed pipe.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // Best-effort process cleanup.
        }

        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Pumps stop when process streams close.
        }

        _process.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private static bool TryReadId(JsonElement message, out long id)
    {
        id = 0;
        if (!message.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.TryGetInt64(out id),
            JsonValueKind.String => long.TryParse(idElement.GetString(), out id),
            _ => false
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
