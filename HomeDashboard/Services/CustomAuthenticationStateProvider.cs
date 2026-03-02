using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace HomeDashboard.Services
{
    public class CustomAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly ProtectedSessionStorage _sessionStorage;
        private ClaimsPrincipal _anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        public CustomAuthenticationStateProvider(ProtectedSessionStorage sessionStorage)
        {
            _sessionStorage = sessionStorage;
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            try
            {
                var userSessionResult = await _sessionStorage.GetAsync<UserSession>("UserSession");
                var userSession = userSessionResult.Success ? userSessionResult.Value : null;
                
                if (userSession == null || string.IsNullOrEmpty(userSession.Username))
                    return await Task.FromResult(new AuthenticationState(_anonymous));

                var claimsIdentity = new ClaimsIdentity(new List<Claim>
                {
                    new Claim(ClaimTypes.Name, userSession.Username),
                    new Claim(ClaimTypes.Role, userSession.Role)
                }, "CustomAuth");

                var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);
                return await Task.FromResult(new AuthenticationState(claimsPrincipal));
            }
            catch
            {
                return await Task.FromResult(new AuthenticationState(_anonymous));
            }
        }

        public async Task MarkUserAsAuthenticated(string username, string role)
        {
            var userSession = new UserSession { Username = username, Role = role };
            await _sessionStorage.SetAsync("UserSession", userSession);

            var claimsIdentity = new ClaimsIdentity(new List<Claim>
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role)
            }, "CustomAuth");

            var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(claimsPrincipal)));
        }

        public async Task MarkUserAsLoggedOut()
        {
            await _sessionStorage.DeleteAsync("UserSession");
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
        }
    }

    public class UserSession
    {
        public string Username { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }
}