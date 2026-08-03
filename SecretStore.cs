using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LightTranslate;

public static class SecretStore
{
    private static readonly string DirectoryPath =
        Environment.GetEnvironmentVariable("LIGHTTRANSLATE_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LightTranslate");

    private static readonly string ApiKeyPath = Path.Combine(DirectoryPath, "api-key.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LightTranslate.ApiKey.v1");

    public static bool HasApiKey()
    {
        try
        {
            return File.Exists(ApiKeyPath) && new FileInfo(ApiKeyPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API Key 不能为空", nameof(apiKey));

        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            AtomicFileStore.SaveBytes(ApiKeyPath, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public static string LoadApiKey()
    {
        if (!File.Exists(ApiKeyPath))
            return TryLoadProtectedFile(ApiKeyPath + ".bak");

        try
        {
            return LoadProtectedFile(ApiKeyPath);
        }
        catch (CryptographicException)
        {
            QuarantineCorruptKey();
            return TryLoadProtectedFile(ApiKeyPath + ".bak");
        }
        catch (IOException)
        {
            return TryLoadProtectedFile(ApiKeyPath + ".bak");
        }
    }

    private static string TryLoadProtectedFile(string path)
    {
        try
        {
            return File.Exists(path) ? LoadProtectedFile(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string LoadProtectedFile(string path)
    {
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            encrypted = File.ReadAllBytes(path);
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static void ClearApiKey()
    {
        DeleteIfExists(ApiKeyPath);
        DeleteIfExists(ApiKeyPath + ".bak");
    }

    private static void QuarantineCorruptKey()
    {
        try
        {
            if (!File.Exists(ApiKeyPath))
                return;

            var quarantinePath = Path.Combine(
                DirectoryPath,
                $"api-key.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.dat");
            File.Move(ApiKeyPath, quarantinePath);
        }
        catch
        {
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
