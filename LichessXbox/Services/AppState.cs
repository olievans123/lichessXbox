using System.Threading.Tasks;
using LichessXbox.Models;

namespace LichessXbox.Services
{
    /// <summary>
    /// Process-wide state: the auth service, API client, and the signed-in account.
    /// Pages reach in through <see cref="Current"/>.
    /// </summary>
    public sealed class AppState
    {
        public static AppState Current { get; } = new AppState();

        public LichessAuthService Auth { get; }
        public LichessApiService Api { get; }
        public LichessAccount Account { get; private set; }

        public bool IsSignedIn => Auth.IsAuthenticated;

        AppState()
        {
            Auth = new LichessAuthService();
            Api = new LichessApiService(Auth);
        }

        /// <summary>Fetch and cache the account if we have a token. Returns null when signed out.</summary>
        public async Task<LichessAccount> EnsureAccountAsync()
        {
            if (!Auth.IsAuthenticated) { Account = null; return null; }
            if (Account != null) return Account;
            Account = await Api.GetAccountAsync();
            return Account;
        }

        public async Task SignOutAsync()
        {
            await Auth.SignOutAsync();
            Account = null;
        }
    }
}
