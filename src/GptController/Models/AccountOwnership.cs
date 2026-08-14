namespace GptController.Models;

public sealed record AccountOwnership(
    AccountOwnershipKind Kind,
    string? OrganizationId = null,
    string? DisplayName = null)
{
    public const string PersonalDisplayName = "个人账号";
    public const string UnknownOrganizationDisplayName = "组织名称未知";
    private const string LegacyUnknownOrganizationDisplayName = "企业账号（名称未知）";

    public static AccountOwnership Personal { get; } =
        new(AccountOwnershipKind.Personal, null, PersonalDisplayName);

    public static AccountOwnership Organization(string? id, string? displayName) =>
        new(
            AccountOwnershipKind.Organization,
            id,
            HasKnownOrganizationName(displayName)
                ? displayName!.Trim()
                : UnknownOrganizationDisplayName);

    public static bool HasKnownOrganizationName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) &&
        !string.Equals(
            displayName.Trim(),
            UnknownOrganizationDisplayName,
            StringComparison.Ordinal) &&
        !string.Equals(
            displayName.Trim(),
            LegacyUnknownOrganizationDisplayName,
            StringComparison.Ordinal);
}
