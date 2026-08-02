namespace GptController.Models;

public sealed record AccountOwnership(
    AccountOwnershipKind Kind,
    string? OrganizationId = null,
    string? DisplayName = null)
{
    public static AccountOwnership Personal { get; } =
        new(AccountOwnershipKind.Personal, null, "个人账号");

    public static AccountOwnership Organization(string? id, string? displayName) =>
        new(
            AccountOwnershipKind.Organization,
            id,
            string.IsNullOrWhiteSpace(displayName) ? "企业账号（名称未知）" : displayName.Trim());
}
