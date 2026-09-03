// FILE: MerkaiTrial.WebApi/Authorization/DemoAuthenticationHandler.cs

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace MerkaiTrial.WebApi.Authorization
{
    public class DemoAuthenticationOptions : AuthenticationSchemeOptions
    {
        public string UserId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
    }

    public class DemoAuthenticationHandler : AuthenticationHandler<DemoAuthenticationOptions>
    {
        public DemoAuthenticationHandler(
            IOptionsMonitor<DemoAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Create claims for demo user
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Options.UserId),
                new Claim("UserId", Options.UserId),
                new Claim("TenantId", Options.TenantId),
                new Claim(ClaimTypes.Name, "Demo User")
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
