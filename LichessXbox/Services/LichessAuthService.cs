using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Security.Credentials;

namespace LichessXbox.Services
{
    /// <summary>
    /// OAuth2 + PKCE login against Lichess. Lichess is an open OAuth provider:
    /// no client secret and no app pre-registration are required for a public
    /// client. Sign-in happens on a second device (the correct pattern for a
    /// console): we build the authorize URL (<see cref="BuildAuthorization"/>),
    /// the user approves it in their phone's real browser, and the code returns to
    /// a LAN loopback callback, which we exchange for a token over PKCE
    /// (<see cref="CompleteAuthAsync"/>). No embedded browser is ever used.
    ///
    /// The bearer token is stored in the Windows <see cref="PasswordVault"/> so the
    /// user stays signed in between sessions.
    /// </summary>
    public sealed class LichessAuthService
    {
        // Any stable string works as a public client_id on Lichess.
        public const string ClientId = "lichess-xbox-app";
        const string Authorize = "https://lichess.org/oauth";
        const string TokenEndpoint = "https://lichess.org/api/token";
        const string Scopes = "board:play challenge:write challenge:read puzzle:read preference:read tournament:write";

        const string VaultResource = "LichessXbox";
        const string VaultUser = "bearer";

        readonly HttpClient _http = new HttpClient();

        public string Token { get; private set; }
        public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

        public LichessAuthService()
        {
            Token = LoadStoredToken();
        }

        /// <summary>Set (or clear) the bearer token directly — used by personal-token sign-in.</summary>
        public void SetToken(string token)
        {
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            if (Token == null) ClearStoredToken();
            else StoreToken(Token);
        }

        /// <summary>Build the authorize URL + PKCE verifier + state for the given redirect URI.</summary>
        public (string url, string verifier, string state) BuildAuthorization(string redirectUri)
        {
            string verifier = Pkce.GenerateCodeVerifier();
            string challenge = Pkce.Challenge(verifier);
            string state = Pkce.RandomState();
            string url = Authorize +
                "?response_type=code" +
                "&client_id=" + Uri.EscapeDataString(ClientId) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                "&code_challenge_method=S256&code_challenge=" + Uri.EscapeDataString(challenge) +
                "&scope=" + Uri.EscapeDataString(Scopes) +
                "&state=" + Uri.EscapeDataString(state);
            return (url, verifier, state);
        }

        /// <summary>Exchange the authorization code (from the LAN callback) for a token. Must use the same redirect URI.</summary>
        public Task<bool> CompleteAuthAsync(string code, string verifier, string redirectUri) =>
            ExchangeCodeAsync(code, verifier, redirectUri);

        async Task<bool> ExchangeCodeAsync(string code, string verifier, string redirect)
        {
            var body = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirect,
                ["client_id"] = ClientId,
            };

            using (var content = new FormUrlEncodedContent(body))
            using (var resp = await _http.PostAsync(TokenEndpoint, content))
            {
                string json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return false;
                var obj = JObject.Parse(json);
                Token = obj.Value<string>("access_token");
                if (string.IsNullOrEmpty(Token)) return false;
                StoreToken(Token);
                return true;
            }
        }

        public async Task SignOutAsync()
        {
            // Best-effort token revocation, then clear local storage.
            if (!string.IsNullOrEmpty(Token))
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Delete, "https://lichess.org/api/token");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                    await _http.SendAsync(req);
                }
                catch { /* ignore */ }
            }
            Token = null;
            ClearStoredToken();
        }

        // ----------------------------------------------------- token storage

        static void StoreToken(string token)
        {
            var vault = new PasswordVault();
            ClearStoredToken();
            vault.Add(new PasswordCredential(VaultResource, VaultUser, token));
        }

        static string LoadStoredToken()
        {
            try
            {
                var vault = new PasswordVault();
                var cred = vault.Retrieve(VaultResource, VaultUser);
                cred.RetrievePassword();
                return cred.Password;
            }
            catch { return null; }
        }

        static void ClearStoredToken()
        {
            try
            {
                var vault = new PasswordVault();
                foreach (var c in vault.FindAllByResource(VaultResource)) vault.Remove(c);
            }
            catch { /* nothing stored */ }
        }
    }
}
