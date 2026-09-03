using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Admin.Web.Services
{
    public sealed class UiContext(IConfiguration cfg)
    {
        public VerticalKind Vertical => Enum.TryParse<VerticalKind>(cfg["Vertical"], true, out var v) ? v : VerticalKind.Generic;
    }
}
