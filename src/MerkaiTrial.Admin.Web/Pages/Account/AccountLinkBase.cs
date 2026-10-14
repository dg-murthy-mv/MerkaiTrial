// =====================================================================
// AccountLinkBase.cs
// Location: MerkaiTrial.Admin.Web/Pages/Account/AccountLinkBase.cs
//
// NEW FILE (086). The web address every sign-in link in an email starts
// with — https://app.merkai.com/Account/SetPassword?token=…
//
// WHY NOT JUST Request.Host
//   Forgot password is anonymous. Anyone can POST it with a forged Host
//   header ("Host: evil.example"), and if the link were built from that,
//   the REAL person would receive a genuine Merkai email whose button
//   points at evil.example — carrying their live reset token. That is a
//   known attack ("password reset poisoning"), and it needs nothing but
//   the victim's email address.
//
//   So the configured address wins:
//
//       Email:AppBaseUrl   on Admin.Web (appsettings or Email__AppBaseUrl)
//                          e.g. https://merkai-trial.azurewebsites.net
//
//   It is not a secret — appsettings.json is fine. Only when it is not
//   set does this fall back to the request's own host, which is what the
//   copy-link boxes always did. Azure only routes a request to the app
//   for host names bound to it, which blunts the attack there; locally it
//   does not matter. SETUP.md for 086 asks for the setting anyway.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Pages.Account
{
    public static class AccountLinkBase
    {
        public static string Resolve(IConfiguration config, HttpRequest request)
        {
            var configured = config["Email:AppBaseUrl"];

            if (!string.IsNullOrWhiteSpace(configured)
                && (configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                return configured.Trim().TrimEnd('/');
            }

            return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        }

        /// <summary>True when the configured address is in use (for the setup hint on admin pages).</summary>
        public static bool IsConfigured(IConfiguration config)
        {
            var configured = config["Email:AppBaseUrl"];
            return !string.IsNullOrWhiteSpace(configured)
                   && (configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                       || configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
