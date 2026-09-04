using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace AccountantApp.Api.Slices.Identity.Application;

public interface ITokenIssuing
{
    /// <summary>Returns the raw token -- the ONLY time it exists. Persist nothing but the hash.</summary>
    string GenerateRawToken();

    /// <summary>Lowercase hex SHA-256. Deterministic: the same input always gives the same output.</summary>
    string HashToken(string rawToken);
}

public sealed class TokenIssuing : ITokenIssuing
{
    public string GenerateRawToken()
    {
        // 32 bytes of CSPRNG output -> 43 URL-safe characters.
        //
        // Never System.Random and never Guid.NewGuid(): a Guid is not a secret. v4 has 122 bits but
        // no guarantee of cryptographic generation, and reaching for one here is the wrong tool for
        // a right-looking reason.
        //
        // Base64Url rather than plain Base64, because the token goes in a URL query string and
        // '+', '/' and '=' survive some URL handling and not others -- the failure is an occasional
        // invalid token nobody can reproduce.
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    public string HashToken(string rawToken)
    {
        // Plain SHA-256, no salt and no work factor, and that is correct. This is not a password:
        // it is 256 bits of uniform random, so there is no dictionary to attack and nothing for a
        // salt to defend against -- and lookup must be a single indexed equality on token_hash,
        // which a per-row salt makes impossible.
        //
        // Constant-time comparison is not needed and must not be added: the comparison happens
        // inside PostgreSQL's index lookup, not in this process. Do not let that rule tempt you
        // into loading candidate rows so you can compare them "safely".
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(hash);
    }
}
