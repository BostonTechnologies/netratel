using System.Security.Cryptography;
using System.Text;

namespace NetRatel.Infrastructure.Persistence;

public static class EnrollmentCodeLookup
{
    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    public static string Hash(string normalizedCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode))).ToLowerInvariant();
}
