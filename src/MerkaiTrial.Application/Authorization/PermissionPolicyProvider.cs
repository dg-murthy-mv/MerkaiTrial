using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Authorization
{
    public class PermissionPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _fallbackPolicyProvider;

        public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        {
            _fallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        {
            return _fallbackPolicyProvider.GetDefaultPolicyAsync();
        }

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        {
            return _fallbackPolicyProvider.GetFallbackPolicyAsync();
        }

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            // Check if this is a permission policy (format: "module.action")
            if (policyName.Contains('.'))
            {
                var parts = policyName.Split('.');
                if (parts.Length == 2)
                {
                    var module = parts[0];
                    var action = parts[1];

                    // Create policy on-demand
                    var policy = new AuthorizationPolicyBuilder()
                        .AddRequirements(new PermissionRequirement(module, action))
                        .Build();

                    return Task.FromResult<AuthorizationPolicy?>(policy);
                }
            }

            // Fall back to default provider for other policies
            return _fallbackPolicyProvider.GetPolicyAsync(policyName);
        }
    }
}
