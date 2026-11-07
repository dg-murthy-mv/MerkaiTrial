// =====================================================================
// FILE: MerkaiTrial.Application/Services/Ai/AiChatClients.cs
//
// NEW (122). Who answers the assistant's questions — chosen by ONE
// setting, so Grok can be swapped for another provider without touching
// anything else:
//
//   Ai__Provider = Grok   xAI's Grok, through its OpenAI-compatible API
//                         (https://api.x.ai/v1). Needs Ai__ApiKey and Ai__Model.
//   Ai__Provider = OpenAICompatible
//                         any other OpenAI-compatible endpoint: set
//                         Ai__BaseUrl as well.
//   Ai__Provider = Fake   a stand-in that answers from a script, never
//                         leaves the server. The test servers use it
//                         (run-e2e.ps1), and so can a laptop with no account.
//   (anything else, or no key)
//                         not set up: the assistant says so and asks nothing.
//
// 123 — TOOLS ("function calling"). The provider can be offered tools
// (AiToolDef: a name, a sentence, a JSON schema of its arguments). Instead
// of answering, it may reply with tool calls; the assistant service runs
// them (read-only, with the person's own access) and sends the results
// back as "tool" messages, and the provider then answers. AiMessage carries
// both new kinds of message: an assistant turn with ToolCalls, and a tool
// result with ToolCallId.
//
// THE KEY IS A SECRET — never in appsettings.json, never pasted anywhere:
//     dotnet user-secrets set "Ai:ApiKey" "xai-..."   (your laptop)
//     App Service → Configuration → Ai__ApiKey         (Azure)
// It lives on the WebApi only. Admin.Web never has it.
// =====================================================================

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MerkaiTrial.Application.Services.Ai;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Grok | OpenAICompatible | Fake | None.</summary>
    public string Provider { get; set; } = "None";
    public string BaseUrl { get; set; } = "https://api.x.ai/v1";
    public string? ApiKey { get; set; }
    /// <summary>The model name from the provider's console (e.g. one of xAI's Grok models).</summary>
    public string? Model { get; set; }
    public int TimeoutSeconds { get; set; } = 45;
    public int MaxAnswerTokens { get; set; } = 700;

    /// <summary>123. How many rounds of tool calls one question may take before it must answer.</summary>
    public int MaxToolRounds { get; set; } = 4;

    /// <summary>Questions a workspace may ask a month, by plan name; "trial" for any trial plan; "default" otherwise.</summary>
    public Dictionary<string, int> MonthlyQuestions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["professional"] = 300,
        ["enterprise"] = 1000,
        ["trial"] = 100,
        ["default"] = 100,
    };

    /// <summary>Questions one person may ask an hour (stops a runaway script).</summary>
    public int PerPersonPerHour { get; set; } = 30;

    public bool IsFake => string.Equals(Provider, "Fake", StringComparison.OrdinalIgnoreCase);
    public bool IsReal =>
        (string.Equals(Provider, "Grok", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Provider, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(BaseUrl);
    public bool IsReady => IsFake || IsReal;
    public string ProviderName => IsFake ? "Fake" : IsReal ? Provider : "None";
}

/// <summary>A tool the provider asked to run: its id (echoed back with the result), name and JSON arguments.</summary>
public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <summary>A tool offered to the provider. Parameters is a JSON schema object.</summary>
public sealed record AiToolDef(string Name, string Description, object Parameters);

/// <summary>
/// One message of the conversation.
///   system / user / assistant  — Content
///   assistant asking for tools  — ToolCalls (Content may be null)
///   tool                        — the result of one call: ToolCallId + Content
/// </summary>
public sealed record AiMessage(string Role, string? Content, string? ToolCallId = null, IReadOnlyList<AiToolCall>? ToolCalls = null);

/// <summary>The provider's reply: an answer (Text), or tool calls to run first.</summary>
public sealed record AiReply(string? Text, string Model, int? InputTokens, int? OutputTokens, IReadOnlyList<AiToolCall>? ToolCalls = null)
{
    public bool WantsTools => ToolCalls is { Count: > 0 };
}

/// <summary>The provider did not answer (down, slow, refused the key). Nothing is counted.</summary>
public sealed class AiProviderException : Exception
{
    public AiProviderException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IAiChatClient
{
    /// <param name="tools">Tools the provider may call; null or empty = none.</param>
    Task<AiReply> CompleteAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolDef>? tools, int maxTokens, CancellationToken ct);
}

/// <summary>xAI Grok, or any OpenAI-compatible endpoint: POST {BaseUrl}/chat/completions.</summary>
public sealed class OpenAiCompatibleChatClient : IAiChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public OpenAiCompatibleChatClient(HttpClient http, Microsoft.Extensions.Options.IOptions<AiOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<AiReply> CompleteAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolDef>? tools, int maxTokens, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages.Select(ToWire).ToList(),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.3,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.Parameters },
            }).ToList();
            body["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        // Per request, not on the client: a key changed in configuration is used straight away.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AiProviderException("The AI provider could not be reached.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Never the body: it can echo the request. Only the status.
                throw new AiProviderException($"The AI provider answered {(int)response.StatusCode}.");
            }

            ChatCompletion? parsed;
            try { parsed = await response.Content.ReadFromJsonAsync<ChatCompletion>(Json, ct); }
            catch (JsonException ex) { throw new AiProviderException("The AI provider sent an answer that could not be read.", ex); }
            if (parsed is null) throw new AiProviderException("The AI provider sent an empty answer.");

            var message = parsed.Choices?.FirstOrDefault()?.Message;
            var calls = message?.ToolCalls?
                .Where(c => !string.IsNullOrWhiteSpace(c.Function?.Name))
                .Select(c => new AiToolCall(c.Id ?? Guid.NewGuid().ToString("N"), c.Function!.Name!, c.Function.Arguments ?? "{}"))
                .ToList();
            var text = message?.Content?.Trim();

            if ((calls is null || calls.Count == 0) && string.IsNullOrEmpty(text))
                throw new AiProviderException("The AI provider sent an empty answer.");

            return new AiReply(text, parsed.Model ?? _options.Model ?? "", parsed.Usage?.PromptTokens, parsed.Usage?.CompletionTokens,
                calls is { Count: > 0 } ? calls : null);
        }
    }

    private static object ToWire(AiMessage m)
    {
        var wire = new Dictionary<string, object?> { ["role"] = m.Role };
        if (m.Role == "tool")
        {
            wire["tool_call_id"] = m.ToolCallId;
            wire["content"] = m.Content ?? "";
        }
        else if (m.ToolCalls is { Count: > 0 })
        {
            wire["content"] = m.Content;   // may be null
            wire["tool_calls"] = m.ToolCalls.Select(c => new
            {
                id = c.Id,
                type = "function",
                function = new { name = c.Name, arguments = c.Arguments },
            }).ToList();
        }
        else
        {
            wire["content"] = m.Content ?? "";
        }
        return wire;
    }

    private sealed class ChatCompletion
    {
        public string? Model { get; set; }
        public List<Choice>? Choices { get; set; }
        public UsageInfo? Usage { get; set; }
    }
    private sealed class Choice { public ChoiceMessage? Message { get; set; } }
    private sealed class ChoiceMessage
    {
        public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<WireToolCall>? ToolCalls { get; set; }
    }
    private sealed class WireToolCall
    {
        public string? Id { get; set; }
        public WireFunction? Function { get; set; }
    }
    private sealed class WireFunction
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }
    private sealed class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    }
}

/// <summary>
/// The stand-in. Answers from a script and never leaves the server:
///   a question containing [[fail]]   → the provider "is down" (AiProviderException)
///   a question containing [[md]]     → an answer written in Markdown, with a
///                                      safe record link and an unsafe one
///                                      (123: the chat's formatting is tested with it)
///   [[tool:NAME {json}]]             → 123: asks for that tool (only if it was
///                                      offered), then answers with what it
///                                      returned: "(Test assistant) NAME returned: …".
///                                      Several [[tool:…]] in one question are
///                                      asked for together. If the tool was not
///                                      offered: "(Test assistant) No tools were offered."
///   anything else                     → "(Test assistant) You asked: …" plus how
///                                      many earlier turns it was given
/// </summary>
public sealed class FakeAiChatClient : IAiChatClient
{
    public const string Marker = "(Test assistant)";

    private static readonly Regex ToolAsk = new(@"\[\[tool:(?<name>[a-z_]+)\s*(?<args>\{.*?\})?\]\]", RegexOptions.Singleline);

    public Task<AiReply> CompleteAsync(IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolDef>? tools, int maxTokens, CancellationToken ct)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == "user");
        var question = lastUser?.Content ?? "";
        if (question.Contains("[[fail]]", StringComparison.Ordinal))
            throw new AiProviderException("The test assistant was asked to fail.");

        // Tool results after the question: answer with them.
        var afterQuestion = lastUser is null ? messages : messages.Skip(messages.ToList().LastIndexOf(lastUser) + 1).ToList();
        var results = afterQuestion.Where(m => m.Role == "tool").ToList();
        if (results.Count > 0)
        {
            var calls = afterQuestion.Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!).ToDictionary(c => c.Id, c => c.Name);
            var text = Marker + " " + string.Join("\n", results.Select(r =>
                $"{(calls.TryGetValue(r.ToolCallId ?? "", out var n) ? n : "tool")} returned: {r.Content}"));
            return Reply(text, question);
        }

        var asks = ToolAsk.Matches(question);
        if (asks.Count > 0)
        {
            var offered = (tools ?? Array.Empty<AiToolDef>()).Select(t => t.Name).ToHashSet();
            var wanted = asks.Select((m, i) => new AiToolCall($"call_{i + 1}", m.Groups["name"].Value,
                m.Groups["args"].Success ? m.Groups["args"].Value : "{}")).Where(c => offered.Contains(c.Name)).ToList();
            if (wanted.Count == 0) return Reply($"{Marker} No tools were offered.", question);
            return Task.FromResult(new AiReply(null, "fake", question.Length / 4 + 50, 20, wanted));
        }

        if (question.Contains("[[md]]", StringComparison.Ordinal))
        {
            return Reply(Marker + " Here is **the plan**:\n\n" +
                         "1. First step\n2. Second step\n\n" +
                         "- a point\n- another point\n\n" +
                         "Open [Safe lead](/Leads/Detail/00000000-0000-0000-0000-000000000001) " +
                         "or [Bad link](javascript:window.__aiLink=1) or [Away](https://example.com/x).", question);
        }

        var earlier = messages.Count(m => m.Role is "user" or "assistant" && m.ToolCalls is null) - 1;
        return Reply($"{Marker} You asked: {question}" + (earlier > 0 ? $" (I can see {earlier} earlier message(s) in this conversation.)" : ""), question);
    }

    private static Task<AiReply> Reply(string text, string question) =>
        Task.FromResult(new AiReply(text, "fake", question.Length / 4 + 50, text.Length / 4));
}
