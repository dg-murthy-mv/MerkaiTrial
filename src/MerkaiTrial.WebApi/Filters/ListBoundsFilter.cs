// =====================================================================
// FILE: MerkaiTrial.WebApi/Filters/ListBoundsFilter.cs
//
// NEW FILE (105 — VUL-18). The same bounds on every list, whatever its
// handler remembers to do.
//
// Most list handlers already clamp their page size (leads, contacts,
// companies, invoices, products, roles, audit, deals…). Some do not —
// countries, tax rates, verticals, tenants — and a page number nobody
// checks can overflow: pageNumber=2147483647 with pageSize=50 makes a
// NEGATIVE skip, and that is a 500, not a list.
//
// So before ANY action runs, its paging arguments are brought inside:
//
//   pageSize, take, limit, top, size     1 … 1000
//   page, pageNumber                     1 … 100 000
//   skip, offset                         0 … 10 000 000
//
// 1000 is the most any screen asks for (dropdowns read whole lists of
// products and deals at 500–1000). A handler's own, smaller cap still
// applies on top — this only removes the extremes.
//
// Only int / long values with those names are touched: action arguments,
// and the settable properties of a query object an action takes whole
// ([FromQuery] LeadListQuery q → q.PageSize) — on GET only, so a body
// sent to save something is never touched. Anything else passes
// through unchanged. Values that are not numbers at all never
// reach here — model binding answers those 400.
// =====================================================================

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MerkaiTrial.WebApi.Filters;

public sealed class ListBoundsFilter : IActionFilter
{
    public const int MaxPageSize  = 1000;
    public const int MaxPage      = 100_000;
    public const int MaxSkip      = 10_000_000;

    private static readonly HashSet<string> SizeNames = new(StringComparer.OrdinalIgnoreCase)
        { "pageSize", "take", "limit", "top", "size" };

    private static readonly HashSet<string> PageNames = new(StringComparer.OrdinalIgnoreCase)
        { "page", "pageNumber" };

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        { "skip", "offset" };

    // Per type: its settable int/long properties with a paging name.
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PagingProperties = new();

    public void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var name in context.ActionArguments.Keys.ToList())
        {
            var value = context.ActionArguments[name];

            if (value is int or long)
            {
                if (Bound(name, value) is { } bounded) context.ActionArguments[name] = bounded;
                continue;
            }

            // 105: a query object taken whole — its PageSize / PageNumber / Skip.
            // Reads only (GET): a body sent to save something is never touched.
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method)) continue;
            if (value is null || value is string || value.GetType().IsValueType) continue;
            foreach (var prop in PagingProperties.GetOrAdd(value.GetType(), FindPagingProperties))
            {
                var current = prop.GetValue(value);
                if (current is null) continue;
                if (Bound(prop.Name, current) is { } bounded) prop.SetValue(value, bounded);
            }
        }
    }

    /// <summary>The value brought inside its bounds, or null when it already was (or the name is not a paging one).</summary>
    private static object? Bound(string name, object value)
    {
        long n = value is int i ? i : (long)value;
        long? bounded =
            SizeNames.Contains(name) ? Math.Clamp(n, 1, MaxPageSize) :
            PageNames.Contains(name) ? Math.Clamp(n, 1, MaxPage) :
            SkipNames.Contains(name) ? Math.Clamp(n, 0, MaxSkip) :
            null;

        if (bounded is null || bounded.Value == n) return null;
        return value is int ? (object)(int)bounded.Value : bounded.Value;
    }

    private static PropertyInfo[] FindPagingProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                     && (p.PropertyType == typeof(int) || p.PropertyType == typeof(long)
                         || p.PropertyType == typeof(int?) || p.PropertyType == typeof(long?))
                     && (SizeNames.Contains(p.Name) || PageNames.Contains(p.Name) || SkipNames.Contains(p.Name))
                     && p.SetMethod is { IsPublic: true })
            .ToArray();

    public void OnActionExecuted(ActionExecutedContext context) { }
}
