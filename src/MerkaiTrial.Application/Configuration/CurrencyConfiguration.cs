using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Configuration
{
    public static class CurrencyConfiguration
    {
        public static readonly List<Currency> Currencies = new()
        {
            new Currency { Code = "USD", Name = "US Dollar", Symbol = "$" },
            new Currency { Code = "EUR", Name = "Euro", Symbol = "€" },
            new Currency { Code = "GBP", Name = "British Pound", Symbol = "£" },
            new Currency { Code = "INR", Name = "Indian Rupee", Symbol = "₹" },
            new Currency { Code = "THB", Name = "Thai Baht", Symbol = "฿" },
            new Currency { Code = "PHP", Name = "Philippine Peso", Symbol = "₱" },
            new Currency { Code = "AED", Name = "UAE Dirham", Symbol = "AED" },
            new Currency { Code = "SGD", Name = "Singapore Dollar", Symbol = "S$" },
            new Currency { Code = "MYR", Name = "Malaysian Ringgit", Symbol = "RM" },
            new Currency { Code = "AUD", Name = "Australian Dollar", Symbol = "A$" },
            new Currency { Code = "CAD", Name = "Canadian Dollar", Symbol = "C$" }
        };

        public static List<string> GetCurrencyCodes() => Currencies.Select(c => c.Code).ToList();

        public static string GetCurrencySymbol(string code)
        {
            return Currencies.FirstOrDefault(c => c.Code == code)?.Symbol ?? code;
        }

        public static string GetCurrencyName(string code)
        {
            return Currencies.FirstOrDefault(c => c.Code == code)?.Name ?? code;
        }
    }

    public class Currency
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
    }
}
