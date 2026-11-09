using System.Security.Cryptography;
using System.Text;

namespace Tokenify.Companion;

/// <summary>
/// This PC's TOKENIFY device id and secret. The secret is encrypted with Windows DPAPI for the current Windows user
/// (another account on the PC, or a copy of the file on another PC, cannot decrypt it). It only allows uploading
/// match summaries for the linked account, and the website can revoke it at any time.
/// </summary>
internal static class Credentials
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TOKENIFY Companion device secret v1");

    public sealed record Device(Guid Id, string Secret);

    public static Device? Load(Store store)
    {
        if (!Guid.TryParse(store.Get("device.id"), out var id) || store.Get("device.secret") is not { Length: > 0 } blob)
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
            return new Device(id, Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("The saved TOKENIFY connection could not be decrypted on this Windows account; please reconnect.");
            return null;
        }
    }

    public static void Save(Store store, Guid id, string secret)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        store.Set("device.id", id.ToString());
        store.Set("device.secret", Convert.ToBase64String(blob));
    }

    public static void Clear(Store store)
    {
        store.Set("device.id", null);
        store.Set("device.secret", null);
        store.Set("account.username", null);
        store.Set("device.name", null);
    }
}
