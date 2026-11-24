using MudBlazor;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Web.Components.Shared.Secrets;

public enum SecretBrowserMode { Manage, PickSecret, PickCredential }
public enum SecretEntryIntent { Details, Edit, Select, Reveal }

internal static class SecretVaultPresentation
{
    public static DialogOptions Options { get; } = new() { FullWidth = true, MaxWidth = MaxWidth.Large, BackdropClick = false };

    public static string Provider(SecretProviderKind kind) => kind switch
    {
        SecretProviderKind.AzureKeyVault => "Azure Key Vault",
        SecretProviderKind.OpenBao => "OpenBao KV v2",
        SecretProviderKind.Infisical => "Infisical",
        _ => "Local encrypted storage"
    };

    public static string Identity(ExecutionIdentityMode mode) => mode switch
    {
        ExecutionIdentityMode.WindowsPassword => "Windows password logon",
        ExecutionIdentityMode.UnixPrivilegedLocal => "Unix privileged local account",
        _ => "Agent service account"
    };

    public static string Reference(SecretReference reference) => reference.Locator +
        (reference.Field is null ? "" : $" · {reference.Field}") +
        (reference.VersionMode == SecretVersionMode.Pinned ? $" · {reference.Version}" : " · Latest at execution");

    public static string Removal(VaultRemovalPreview preview) => preview.Request.Kind switch
    {
        VaultRemovalKind.DisableConnection => $"Disable {preview.DisplayName}?",
        VaultRemovalKind.DeleteConnection => $"Remove the saved connection {preview.DisplayName}?",
        VaultRemovalKind.CredentialProfile => $"Delete the credential profile {preview.DisplayName}?",
        _ => preview.DeletionMode switch
        {
            SecretDeletionMode.RecoverableEntry => $"Start recoverable deletion of {preview.DisplayName} in Azure Key Vault?",
            SecretDeletionMode.SoftDeleteCurrentVersion => $"Soft-delete version {preview.Request.Secret!.ExpectedVersion} of {preview.DisplayName}?",
            _ => $"Delete {preview.DisplayName} and its available versions?"
        }
    };
}
