// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/Roles/Index.cshtml.cs
//
// Create the folders: Pages/Settings/Roles/
//
// The tenant-facing twin of /Admin/Roles. Same IRoleService calls � the
// API scopes by tenant via IRoleScope, so this page needs no tenant logic
// of its own. What differs is WHO may reach it:
//
//   /Admin/Roles     SuperAdmin only (AuthorizeFolder), shows all tenants
//   /Settings/Roles  any user with roles.read, shows their tenant only
//
// It inherits AuthorizedPageModel rather than PageModel so the roles.*
// permissions are actually enforced and CanCreate/CanUpdate/CanDelete are
// available to the view. The /Admin version is a plain PageModel because
// the folder-level SuperAdmin policy is its only gate.
//
// 096 — THE SHARED LIST PIECES, AND TWO SMALL BUGS
//   • _ListToolbar (search, Built-in / Your roles, the count) and _Pager
//     replace the search card and the old _Pagination partial. `Page` is
//     PageNumber, like every other list. 10 per page by default; the size
//     a person picks is remembered on their browser.
//   • Delete comes back to the same page of the same search and filter.
//   • A role name with an apostrophe broke Delete: the confirm text sat
//     inside onsubmit="return confirm('…')", the script threw, and the
//     form posted WITHOUT asking. It is a data-confirm attribute now.
//   • A failed load used TempData on a response that renders, so the
//     error showed one page late. It is PageError now.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;            // 096
using MerkaiTrial.Admin.Web.Services.Roles;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Roles;

public class IndexModel : AuthorizedPageModel
{
    private readonly IRoleService _roleService;

    protected override string ModuleName => Modules.Roles;

    public IndexModel(
        IRoleService roleService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _roleService = roleService;
    }

    public PaginatedRolesResponse Roles { get; set; } = null!;
    public RoleStatsDto Stats { get; set; } = null!;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    /// <summary>"", "true" (built-in) or "false" (your roles). 096: a string — a bool? turns anything else into a hidden binding error.</summary>
    [BindProperty(SupportsGet = true)] public string? IsSystemRole { get; set; }

    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;              // 096 — was Page
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = PagerVm.DefaultSize;

    /// <summary>096. A failed load, for THIS response.</summary>
    public string? PageError { get; private set; }

    private bool? SystemOnly =>
        string.Equals(IsSystemRole, "true",  StringComparison.OrdinalIgnoreCase) ? true :
        string.Equals(IsSystemRole, "false", StringComparison.OrdinalIgnoreCase) ? false : null;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search) || SystemOnly.HasValue;

    // ── 096: the shared toolbar and pager ─────────────────────────────

    private int TotalCount => Roles?.TotalCount ?? 0;

    public Dictionary<string, string> FilterRoute()
    {
        var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Search)) route[nameof(Search)]       = Search.Trim();
        if (SystemOnly is { } sys)              route[nameof(IsSystemRole)] = sys ? "true" : "false";
        return route;
    }

    public Dictionary<string, string> StateRoute()
    {
        var route = FilterRoute();
        route[nameof(PageNumber)] = PageNumber.ToString();
        route[nameof(PageSize)]   = PageSize.ToString();
        return route;
    }

    public ListToolbarVm Toolbar => new()
    {
        SearchName        = nameof(Search),
        SearchValue       = Search,
        SearchPlaceholder = "Role name or description",
        SearchLabel       = "Search roles",
        SearchWidth       = "300px",
        Filters = new List<ListFilterVm>
        {
            new()
            {
                Name    = nameof(IsSystemRole),
                Label   = "Filter by kind of role",
                Options = new List<SelectListItem>
                {
                    new() { Value = "",      Text = "All roles",  Selected = SystemOnly is null },
                    new() { Value = "true",  Text = "Built-in",   Selected = SystemOnly == true },
                    new() { Value = "false", Text = "Your roles", Selected = SystemOnly == false }
                }
            }
        },
        Carry        = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
        ClearRoute   = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
        IsFiltered   = IsFiltered,
        TotalCount   = TotalCount,
        FirstRow     = TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1,
        LastRow      = Math.Min(PageNumber * PageSize, TotalCount),
        NounSingular = "role",
        NounPlural   = "roles"
    };

    public PagerVm Pager => new()
    {
        Page       = PageNumber,
        PageSize   = PageSize,
        TotalCount = TotalCount,
        TotalPages = TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize),
        PageField  = nameof(PageNumber),
        Route      = FilterRoute()
    };

    private RouteValueDictionary ReturnRoute()
    {
        var values = new RouteValueDictionary();
        foreach (var (k, v) in FilterRoute()) values[k] = v;
        values[nameof(PageNumber)] = Math.Max(PageNumber, 1);
        values[nameof(PageSize)]   = PagerVm.Sizes.Contains(PageSize) ? PageSize : PagerVm.DefaultSize;
        return values;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        // 096 — the URL's size, else this browser's last choice, else 10.
        PageSize   = ListPreferences.PageSize(HttpContext, "roles");
        PageNumber = Math.Max(PageNumber, 1);

        // Drives which buttons the view renders. A user with roles.read but
        // not roles.create should see the list without a Create button �
        // showing a button that always fails is worse than hiding it.
        await InitializePermissionsAsync();

        await LoadDataAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
        if (permissionCheck != null) return permissionCheck;

        try
        {
            await _roleService.DeleteAsync(id);
            TempData["SuccessMessage"] = "Role deleted successfully.";
        }
        catch (InvalidOperationException ex)
        {
            // "Cannot delete system roles" and "role is assigned to users"
            // both arrive here � both are messages worth showing verbatim.
            Logger.LogWarning(ex, "Failed to delete role {RoleId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            // The API returns 404 for another tenant's role � deliberately
            // indistinguishable from "does not exist".
            Logger.LogWarning(ex, "Role {RoleId} not found for this tenant", id);
            TempData["ErrorMessage"] = "That role could not be found.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete role {RoleId}", id);
            TempData["ErrorMessage"] = "Failed to delete the role. Please try again.";
        }

        return RedirectToPage(ReturnRoute());   // 096 — same page, same search
    }

    private async Task LoadDataAsync()
    {
        // 096 — the tiles and the list load separately.
        try
        {
            Stats = await _roleService.GetStatsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Role stats could not be loaded");
            Stats = new RoleStatsDto(0, 0, 0, 0);
        }

        try
        {
            Roles = await _roleService.GetPaginatedAsync(PageNumber, PageSize, Search, SystemOnly);

            // A page past the end (the last role on it deleted) → the last real page.
            var lastPage = Roles.TotalCount == 0 ? 1 : (int)Math.Ceiling(Roles.TotalCount / (double)PageSize);
            if (PageNumber > lastPage)
            {
                PageNumber = lastPage;
                Roles = await _roleService.GetPaginatedAsync(PageNumber, PageSize, Search, SystemOnly);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load roles for tenant");
            PageError = "Failed to load roles. Please try again.";        // 096 — not TempData
            Roles = new PaginatedRolesResponse(new List<RoleListItem>(), 0, 1, PageSize);
        }
    }
}

