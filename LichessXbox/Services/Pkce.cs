using System;
using System.Security.Cryptography;
using System.Text;

namespace LichessXbox.Services
{
    /// <summary>RFC 7636 PKCE helpers for the OAuth2 public-client flow Lichess uses.</summary>
    public static class Pkce
    {
        public static string GenerateCodeVerifier()
        {
            var bytes = new byte[64];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        public static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
                return Base64Url(hash);
            }
        }

        public static string RandomState()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
