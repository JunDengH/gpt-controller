using System.Text.Json;

var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")
    ?? Environment.CurrentDirectory;
var scenarioFile = Path.Combine(codexHome, "fake-scenario.txt");
var scenario = File.Exists(scenarioFile)
    ? (await File.ReadAllTextAsync(scenarioFile)).Trim()
    : "normal";

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    var method = root.TryGetProperty("method", out var methodElement)
        ? methodElement.GetString()
        : null;
    var id = root.TryGetProperty("id", out var idElement)
        ? idElement.Clone()
        : (JsonElement?)null;

    switch (method)
    {
        case "initialize":
            await WriteResponseAsync(id, new
            {
                serverInfo = new
                {
                    name = "fake-codex-app-server",
                    version = "1.0"
                }
            });
            break;

        case "initialized":
            break;

        case "account/read" when scenario == "crash-on-account-read":
            Environment.ExitCode = 23;
            return;

        case "account/read":
            await WriteResponseAsync(id, new
            {
                account = new
                {
                    email = "protocol@example.com",
                    planType = "plus",
                    accountId = "account-protocol"
                }
            });
            break;

        case "account/rateLimits/read":
            await WriteResponseAsync(id, new
            {
                rateLimitsByLimitId = new
                {
                    codex = new
                    {
                        planType = "pro",
                        primary = new
                        {
                            usedPercent = 12,
                            windowDurationMins = 300
                        },
                        secondary = new
                        {
                            usedPercent = 28,
                            windowDurationMins = 10_080,
                            resetsAt = 1_900_000_000
                        }
                    }
                }
            });
            break;

        case "account/login/start":
            if (scenario == "login-notification-before-response")
            {
                await WriteMessageAsync(new
                {
                    method = "account/login/completed",
                    @params = new
                    {
                        loginId = "login-protocol",
                        success = true
                    }
                });
            }

            await WriteResponseAsync(id, new
            {
                loginId = "login-protocol",
                authUrl = "https://example.invalid/oauth"
            });
            break;

        default:
            if (id.HasValue)
            {
                await WriteMessageAsync(new
                {
                    id = id.Value,
                    error = new
                    {
                        code = -32601,
                        message = $"Unknown method: {method}"
                    }
                });
            }

            break;
    }
}

static Task WriteResponseAsync(JsonElement? id, object result) =>
    WriteMessageAsync(new
    {
        id,
        result
    });

static async Task WriteMessageAsync(object message)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
    await Console.Out.FlushAsync();
}
