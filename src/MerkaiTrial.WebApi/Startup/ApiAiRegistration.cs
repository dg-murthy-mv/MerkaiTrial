// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiAiRegistration.cs
//
// NEW (122). The AI assistant. Settings (section "Ai"):
//
//   Ai__Provider   Grok | OpenAICompatible | Fake      (missing = not set up)
//   Ai__ApiKey     SECRET — user-secrets / App Service only, never appsettings.json
//   Ai__Model      the model name from the provider's console
//   Ai__BaseUrl    https://api.x.ai/v1 for Grok (the default)
//   Ai__MonthlyQuestions__professional / __enterprise / __trial / __default
//   Ai__PerPersonPerHour
//   Ai__MaxToolRounds   123: rounds of record look-ups per question (default 4)
//
// The test servers run with Ai__Provider=Fake and a blank key (run-e2e.ps1),
// so a test never reaches a real provider.
// =====================================================================

using MerkaiTrial.Application.Services.Ai;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiAiRegistration
{
    public static IServiceCollection AddApiAiAssistant(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiOptions>(config.GetSection(AiOptions.SectionName));
        var options = config.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();

        if (options.IsReal)
        {
            services.AddHttpClient<IAiChatClient, OpenAiCompatibleChatClient>(http =>
            {
                http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 120));
            });
        }
        else
        {
            // Fake answers from a script; with no provider set up the
            // service refuses before it is ever called.
            services.AddSingleton<IAiChatClient, FakeAiChatClient>();
        }

        services.AddScoped<AiDataTools>();          // 123: read-only record tools
        services.AddScoped<AiActions>();            // 125: prepared changes (confirmed by the person)
        services.AddScoped<AiAssistantService>();
        return services;
    }
}
