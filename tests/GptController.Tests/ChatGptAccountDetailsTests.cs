using System.Net;
using System.Text;
using System.Text.Json;
using GptController.Models;
using GptController.Services;
using GptController.ViewModels;

namespace GptController.Tests;

public sealed class ChatGptAccountDetailsTests
{
    [Theory]
    [InlineData("prolite", MembershipPlan.Pro5x, "Pro 5x")]
    [InlineData("pro", MembershipPlan.Pro10x, "Pro 10x")]
    [InlineData("promax", MembershipPlan.Pro25x, "Pro 25x")]
    public async Task LiveAccountPlanKeepsTierEvenWhenCliReportsGenericPro(string live, MembershipPlan expected, string display)
    {
        using var http = new HttpClient(new Handler(_ => Json($$"""{"accounts":[{"id":"target","plan_type":"{{live}}","structure":"personal"}]}""")));
        var service = new AccountMetadataService(new ChatGptAccountDetailsClient(http));
        var result = await service.ResolveAsync(Claims("target", "pro"), "token-test", "pro", new AccountReadMetadata("test@example.com", "pro"));
        Assert.Equal(expected, result.MembershipPlan);
        Assert.Equal(display, Profile(result).PlanDisplayName);
        Assert.True(result.AccountMetadataVerified);
    }

    [Fact]
    public async Task LiveWorkspaceNameReplacesOldCacheWithExactAccountMatch()
    {
        var handler = new Handler(_ => Json("""
            {"default_account_id":"other","accounts":[
            {"id":"other","name":"错误团队","plan_type":"team","structure":"workspace"},
            {"id":"target","name":"新团队名称","plan_type":"team","structure":"workspace"}]}
            """));
        using var http = new HttpClient(handler);
        var cached = Profile(new ResolvedAccountMetadata("test@example.com", "target", MembershipPlan.Business,
            AccountOwnership.Organization("target", "旧团队名称")));
        var result = await new AccountMetadataService(new ChatGptAccountDetailsClient(http))
            .ResolveAsync(Claims("target", "team"), "token-test", cached: cached);
        Assert.Equal("新团队名称", result.Ownership.DisplayName);
        Assert.Equal("target", handler.Headers.Single().AccountId);
        Assert.Equal("Bearer", handler.Headers.Single().Scheme);
        Assert.Equal("https://chatgpt.com/backend-api/wham/accounts/check", handler.Uris.Single().AbsoluteUri);
        Assert.Equal("新团队名称", new AccountCardViewModel(Profile(result)).CompanyDisplayName);
    }

    [Fact]
    public void VersionedMapDoesNotChooseDefaultPersonalOrDifferentWorkspace()
    {
        using var payload = JsonDocument.Parse("""
            {"default_account_id":"personal","account_ordering":["personal","other","target"],"accounts":{
            "personal":{"account":{"account_id":"personal","name":"Personal","structure":"personal","plan_type":"pro"}},
            "other":{"account":{"account_id":"other","name":"其他团队","structure":"workspace","plan_type":"team"}},
            "target":{"account":{"account_id":"target","name":"正确团队","structure":"workspace","plan_type":"team"}}}}
            """);
        var result = ChatGptAccountDetailsClient.Parse(payload.RootElement, "target")!;
        Assert.Equal("正确团队", result.WorkspaceName);
        Assert.Equal(AccountWorkspaceKind.Workspace, result.WorkspaceKind);
    }

    [Fact]
    public async Task MissingNameUsesRicherWebAccountResponse()
    {
        var handler = new Handler(index => Json(index == 0
            ? """{"accounts":[{"id":"target","plan_type":"team","structure":"workspace","name":null}]}"""
            : """{"accounts":{"target":{"account":{"account_id":"target","plan_type":"team","structure":"workspace","name":"补全名称"}}}}"""));
        using var http = new HttpClient(handler);
        var result = await new ChatGptAccountDetailsClient(http).ReadAsync("token-test", "target");
        Assert.Equal("补全名称", result.WorkspaceName);
        Assert.Equal("/backend-api/accounts/check/v4-2023-04-27", handler.Uris[1].AbsolutePath);
        Assert.All(handler.Uris, uri => Assert.Equal("chatgpt.com", uri.Host));
    }

    [Fact]
    public async Task UnavailableSessionsDoNotPreventNameLookupDuringResolution()
    {
        using var http = new HttpClient(new Handler(_ => Json("""{"accounts":[{"id":"target","name":"真实名称","plan_type":"team","structure":"workspace"}]}""")));
        var result = await new AccountMetadataService(new ChatGptAccountDetailsClient(http))
            .ResolveAsync(Claims("target", "team"), "token-test", accountRead: new AccountReadMetadata("test@example.com", "business"));
        Assert.Equal("真实名称", result.Ownership.DisplayName);
    }

    [Fact]
    public async Task LookupFailureRetainsVerifiedSameAccountNameAndExposesOnlyErrorCode()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("token-secret-never-expose") }));
        var cached = Profile(new ResolvedAccountMetadata("test@example.com", "target", MembershipPlan.Business,
            AccountOwnership.Organization("target", "已验证团队"), AccountMetadataVerified: true));
        var result = await new AccountMetadataService(new ChatGptAccountDetailsClient(http))
            .ResolveAsync(Claims("target", "team"), "token-test", cached: cached);
        Assert.Equal("已验证团队", result.Ownership.DisplayName);
        Assert.Equal("account_details_http_403", result.AccountMetadataErrorCode);
        Assert.DoesNotContain("secret", new AccountCardViewModel(Profile(result)).AccountMetadataStatusText);
    }

    [Fact]
    public async Task FailedNameFallbackStillPreservesVerifiedLivePlan()
    {
        var handler = new Handler(index => index == 0
            ? Json("""{"accounts":[{"id":"target","name":null,"structure":"workspace","plan_type":"team"}]}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);
        var result = await new AccountMetadataService(new ChatGptAccountDetailsClient(http))
            .ResolveAsync(Claims("target", "plus"), "token-test");
        Assert.Equal(MembershipPlan.Business, result.MembershipPlan);
        Assert.Equal(AccountOwnershipKind.Organization, result.Ownership.Kind);
        Assert.Equal("account_details_http_403", result.AccountMetadataErrorCode);
    }

    [Fact]
    public void ConflictingMapIdentityIsRejectedRatherThanBorrowingName()
    {
        using var payload = JsonDocument.Parse("""{"accounts":{"target":{"account":{"account_id":"other","name":"错误团队"}}}}""");
        var error = Assert.Throws<ChatGptAccountDetailsException>(() => ChatGptAccountDetailsClient.Parse(payload.RootElement, "target"));
        Assert.Equal("account_details_identity_mismatch", error.ErrorCode);
    }

    [Fact]
    public async Task RedirectResponsesDoNotCreateMetadataOrFollowAnUntrustedOrigin()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://untrusted.example/") } });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ChatGptAccountDetailsException>(() => new ChatGptAccountDetailsClient(http).ReadAsync("token-test", "target"));
        Assert.Equal("account_details_http_302", error.ErrorCode);
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoCachedSuccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var http = new HttpClient(new Handler(_ => Json("{}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatGptAccountDetailsClient(http).ReadAsync("token-test", "target", cts.Token));
    }

    [Fact]
    public async Task MismatchedAppServerAccountIsNotHiddenByBackendLookup()
    {
        var handler = new Handler(_ => Json("{}"));
        using var http = new HttpClient(handler);
        var result = await new AccountMetadataService(new ChatGptAccountDetailsClient(http))
            .ResolveAsync(Claims("target", "team"), "token-test", accountRead: new AccountReadMetadata(null, "team", "other"));
        Assert.Equal("other", result.AccountId);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task VerifiedWorkspaceNameCanBeDisplayedWhenPlanCodeIsNew()
    {
        using var http = new HttpClient(new Handler(_ => Json("""{"accounts":[{"id":"target","name":"新类型团队","plan_type":"future-workspace","structure":"workspace"}]}""")));
        var result = await new AccountMetadataService(new ChatGptAccountDetailsClient(http)).ResolveAsync(Claims("target", "team"), "token-test");
        Assert.Equal(MembershipPlan.Unknown, result.MembershipPlan);
        Assert.Equal("新类型团队", new AccountCardViewModel(Profile(result)).CompanyDisplayName);
    }

    private static AuthClaims Claims(string id, string plan) => new("test@example.com", id, null, plan, [], null, null);
    private static AccountProfile Profile(ResolvedAccountMetadata result) => new()
    {
        Nickname = "Test", Email = result.Email, AccountId = result.AccountId,
        MembershipPlan = result.MembershipPlan, RawPlanType = result.RawPlanType, Ownership = result.Ownership,
        AccountMetadataVerified = result.AccountMetadataVerified, AccountMetadataErrorCode = result.AccountMetadataErrorCode
    };
    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public List<(string? AccountId, string? Scheme)> Headers { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uris.Add(request.RequestUri!);
            Headers.Add((request.Headers.GetValues("ChatGPT-Account-Id").Single(), request.Headers.Authorization?.Scheme));
            return Task.FromResult(response(Uris.Count - 1));
        }
    }
}
