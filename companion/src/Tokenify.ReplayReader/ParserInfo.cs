using System.Security.Cryptography;
using System.Text;

namespace Tokenify.ReplayReader;

public static class ParserInfo
{
    /// <summary>
    /// Version of the counting logic. Bump it whenever the parser could produce a different count for the same
    /// replay: cached results from older versions are re-read (and re-sent; the server keeps the old value for
    /// audit and never overwrites a user's own correction).
    /// </summary>
    public const string Version = "2.0.0";

    /// <summary>
    /// The identifier TOKENIFY stores for a Fortnite account: a SHA-256 of the Epic account id. The same on every
    /// PC (so a reinstall or a second PC is recognised) but not reversible into the account id.
    /// </summary>
    public static string PlayerHash(string epicAccountId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("tokenify-fortnite-player:" + epicAccountId.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(bytes);
    }
}
