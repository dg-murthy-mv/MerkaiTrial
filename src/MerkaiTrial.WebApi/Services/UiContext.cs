// src/MerkaiTrial.Admin.Web/Services/UiContext.cs  (or a shared project you already use)
using MerkaiTrial.Domain.Enums;

public sealed class UiContext
{
    private readonly IConfiguration _cfg;
    public UiContext(IConfiguration cfg) => _cfg = cfg;

    // appsettings.json:  "Vertical": "Generic" | "Finance" | "Logistics"
    public VerticalKind Vertical =>
        Enum.TryParse<VerticalKind>(_cfg["Vertical"], true, out var v) ? v : VerticalKind.Generic;

    // If your DB CHECK constraint doesn't include finance/logistics extra stages yet,
    // keep this true to map to existing DB-safe stages.
    public bool UseCompatStages =>
        bool.TryParse(_cfg["UseCompatStages"], out var b) ? b : true;
}
