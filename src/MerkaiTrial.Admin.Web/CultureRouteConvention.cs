using Microsoft.AspNetCore.Mvc.ApplicationModels;

public sealed class CultureRouteConvention : IPageRouteModelConvention
{
    public void Apply(PageRouteModel model)
    {
        for (var i = 0; i < model.Selectors.Count; i++)
        {
            var sel = model.Selectors[i];
            var template = sel.AttributeRouteModel?.Template ?? string.Empty;

            // 1) Root Index (empty template) => "{culture=en}"
            if (string.IsNullOrWhiteSpace(template))
            {
                sel.AttributeRouteModel ??= new AttributeRouteModel();
                sel.AttributeRouteModel.Template = "{culture=en}";
                continue;
            }

            // Normalize
            template = template.TrimStart('/');

            // 2) Already culture-prefixed? leave it alone
            if (template.StartsWith("{culture}", StringComparison.OrdinalIgnoreCase))
            {
                sel.AttributeRouteModel!.Template = template;
                continue;
            }

            // 3) Prefix everything else: "{culture=en}/..."
            sel.AttributeRouteModel!.Template = "{culture=en}/" + template;
        }
    }
}
