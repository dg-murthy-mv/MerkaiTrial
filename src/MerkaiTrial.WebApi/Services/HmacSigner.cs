using System.Security.Cryptography;
using System.Text;

namespace MerkaiTrial.WebApi.Services;

public static class HmacSigner
{
    public static string Sign(string payload, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = h.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(sig).ToLowerInvariant();
    }

    public static bool Verify(string payload, string signature, string secret)
        => Sign(payload, secret).Equals(signature, StringComparison.OrdinalIgnoreCase);
}
