namespace MerkaiTrial.WebApi.Services;

public static class TemplateEngine
{
    // simple {{Token}} replacement
    public static string Render(string template, IDictionary<string, string> data)
    {
        foreach (var kv in data)
            template = template.Replace("{{" + kv.Key + "}}", kv.Value ?? "");
        return template;
    }
}
