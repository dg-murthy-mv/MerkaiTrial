// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Shared/ValidationAttributes.cs
//
// NEW (095). Three attributes for page input models, each checking ONE
// rule from InputRules (MerkaiTrial.Application/Validation) — on the
// server when the form posts, and in the browser before it does:
//
//     [PhoneNumber(CountryCodeProperty = nameof(Country))]
//     [EmailRule]
//     [Money("the expected value", AllowZero = false)]
//
// The browser half: each attribute writes data-val-merkai… attributes on
// its input (IClientModelValidator), which _ValidationScriptsPartial
// reads. The sentences come from InputRules too, so the browser and the
// server say the same words.
//
// [PhoneNumber]'s CountryCodeProperty names a property on the SAME model
// holding a country CODE ("IN", "TH" …), so a number typed without +
// can be checked against that country's format. Leave it out when the
// model holds the country some other way (a lead's CountryId is a Guid);
// the page model then makes the country check itself — see Leads/Create.
// =====================================================================

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MerkaiTrial.Application.Validation;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    internal static class ClientRules
    {
        public static void Merge(IDictionary<string, string> attributes, string key, string value)
        {
            if (!attributes.ContainsKey(key)) attributes.Add(key, value);
        }

        public static ValidationResult Fail(string message, ValidationContext ctx) =>
            new(message, ctx.MemberName is null ? null : new[] { ctx.MemberName });
    }

    /// <summary>
    /// 095. For page models whose input model has no attributes to hang the
    /// rules on (Products binds the API's own DTO): one line per field,
    /// each message under its own box.
    /// </summary>
    public static class ModelStateRules
    {
        public static void AddIf(this Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState,
            string key, string? error)
        {
            if (string.IsNullOrEmpty(error)) return;
            // One message per box: a binder error ("The value 'abc' is not
            // valid") already says the box is wrong.
            if (modelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0) return;
            modelState.AddModelError(key, error);
        }
    }

    /// <summary>095. A phone number: see InputRules.Phone.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class PhoneNumberAttribute : ValidationAttribute, IClientModelValidator
    {
        /// <summary>A property on the same model holding the record's country code, or null.</summary>
        public string? CountryCodeProperty { get; set; }

        protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
        {
            string? code = null;
            if (CountryCodeProperty is not null && ctx.ObjectInstance is not null)
                code = ctx.ObjectType.GetProperty(CountryCodeProperty)?.GetValue(ctx.ObjectInstance) as string;

            var error = InputRules.Phone(value as string, code);
            return error is null ? ValidationResult.Success : ClientRules.Fail(error, ctx);
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            ClientRules.Merge(context.Attributes, "data-val", "true");
            ClientRules.Merge(context.Attributes, "data-val-merkaiphone", InputRules.PhoneCharactersMessage);
        }
    }

    /// <summary>095. An email address: see InputRules.Email. Stricter than [EmailAddress], which only wants an @.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class EmailRuleAttribute : ValidationAttribute, IClientModelValidator
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
        {
            var error = InputRules.Email(value as string);
            return error is null ? ValidationResult.Success : ClientRules.Fail(error, ctx);
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            ClientRules.Merge(context.Attributes, "data-val", "true");
            ClientRules.Merge(context.Attributes, "data-val-merkaiemail", InputRules.EmailMessage);
        }
    }

    /// <summary>095. An amount of money: not negative, at most InputRules.MaxMoney, and more than 0 unless AllowZero.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class MoneyAttribute : ValidationAttribute, IClientModelValidator
    {
        /// <summary>How the sentence names the field: "the expected value".</summary>
        public string Label { get; }

        public bool AllowZero { get; set; } = true;

        public MoneyAttribute(string label) => Label = label;

        protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
        {
            decimal? amount = value switch
            {
                null      => null,
                decimal d => d,
                double  x => (decimal)x,
                int     i => i,
                _         => null
            };

            var error = InputRules.Money(amount, Label, AllowZero);
            return error is null ? ValidationResult.Success : ClientRules.Fail(error, ctx);
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            ClientRules.Merge(context.Attributes, "data-val", "true");
            ClientRules.Merge(context.Attributes, "data-val-merkaimoney", Label);
            ClientRules.Merge(context.Attributes, "data-val-merkaimoney-allowzero", AllowZero ? "true" : "false");
            ClientRules.Merge(context.Attributes, "data-val-merkaimoney-max",
                InputRules.MaxMoney.ToString(CultureInfo.InvariantCulture));
        }
    }
}
