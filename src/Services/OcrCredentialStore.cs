using Windows.Security.Credentials;

namespace ZSnaper.Services;

/// <summary>
/// Keeps the OCR API key in Windows Credential Locker instead of config.json.
/// </summary>
internal static class OcrCredentialStore
{
    private const string ResourceName = "ZSnaper.OcrApi";
    private const string UserName = "default";

    public static bool HasApiKey => !string.IsNullOrEmpty(TryGetApiKey());

    public static string? TryGetApiKey()
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(ResourceName, UserName);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        PasswordVault vault = new();
        RemoveExisting(vault);
        vault.Add(new PasswordCredential(ResourceName, UserName, apiKey.Trim()));
    }

    public static void ClearApiKey()
    {
        PasswordVault vault = new();
        RemoveExisting(vault);
    }

    private static void RemoveExisting(PasswordVault vault)
    {
        try
        {
            PasswordCredential existing = vault.Retrieve(ResourceName, UserName);
            vault.Remove(existing);
        }
        catch
        {
            // Credential Locker reports a missing entry through an exception.
        }
    }
}
