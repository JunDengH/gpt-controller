namespace GptController.Models;

public sealed record AccountOwnership(
    AccountOwnershipKind Kind,
    string? OrganizationId = null,
    string? DisplayName = null)
{
    public const string UnknownOrganizationDisplayName = "组织名称未知";
    private const string LegacyUnknownOrganizationDisplayName = "企业账号（名称未知）";

    private static readonly HashSet<string> InvalidOrganizationDisplayNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Personal",
            "Personal account",
            "个人账号",
            UnknownOrganizationDisplayName,
            LegacyUnknownOrganizationDisplayName
        };

    public static AccountOwnership Personal { get; } =
        new(AccountOwnershipKind.Personal);

    public static AccountOwnership Organization(string? id, string? displayName) =>
        new(
            AccountOwnershipKind.Organization,
            id,
            HasKnownOrganizationName(displayName)
                ? displayName!.Trim()
                : UnknownOrganizationDisplayName);

    public static bool HasKnownOrganizationName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) &&
        !InvalidOrganizationDisplayNames.Contains(displayName.Trim());
}
