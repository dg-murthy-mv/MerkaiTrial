// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/World.cs
//
// What the run created before the tests: fresh workspaces and the people
// in them. Built by GlobalSetup → WorldBuilder (A and B, before the first
// test), by WorkspaceSettingsTests (C, before its own tests) and by
// the first of CustomFieldTests / NotificationTests to run (D).
//
//   Workspace A — India / INR / Asia/Kolkata. The main one:
//       admin (tenant admin), manager (Sales Manager), rep (Sales Rep),
//       viewer (Viewer), resetme (Viewer — the password-reset test changes
//       this account's password, so nothing else signs in as it)
//   Workspace B — India / INR, only an admin. Used to prove A cannot see
//       B's records, and for the invite test.
//   Workspace C — Thailand / THB / Asia/Bangkok (088). The settings
//       workspace: admin, manager, rep, rep2. Its tests change the sales
//       process, approval rules, record visibility and the company
//       profile — workspace-wide settings that would otherwise leak into
//       the tests above. Bangkok is UTC+7 on purpose: a test machine in
//       India is UTC+5:30, so "shown in the workspace's time" and "shown
//       in the computer's time" give different answers.
//   Workspace D — India / INR (089). The custom field and notification
//       workspace: admin, rep, rep2. Its tests add custom fields (one of
//       them required), change people's notification preferences and lock
//       a workspace notification default — all things that would change
//       how leads behave for the tests above.
//
// Every run makes new workspaces with a time stamp in the name, so runs
// never trip over each other's data. Old ones simply pile up in the test
// database; restore it from a fresh backup whenever you like.
// =====================================================================

namespace MerkaiTrial.E2E.Infrastructure;

public sealed record TestUser(string Key, string Email, string Password, string FirstName, string LastName)
{
    public string FullName => $"{FirstName} {LastName}";

    /// <summary>Password can change (the reset test); the rest cannot.</summary>
    public string Password { get; set; } = Password;
}

public sealed class TestTenant
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required TestUser Admin { get; init; }

    /// <summary>As the country drop-down says it, e.g. "India".</summary>
    public string Country { get; init; } = "India";

    /// <summary>ISO code, e.g. "INR".</summary>
    public string Currency { get; init; } = "INR";

    /// <summary>The workspace's offset from UTC. Neither India nor
    /// Thailand has daylight saving, so a fixed offset is exact.</summary>
    public TimeSpan UtcOffset { get; init; } = TimeSpan.FromHours(5.5);

    /// <summary>A phone number in this country's format, for new leads.</summary>
    public string SamplePhone { get; init; } = "+919800000000";

    public Dictionary<string, TestUser> People { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The role label each seeded person was given, as Settings →
    /// Users showed it ("Sales Representative"). Record Visibility lists
    /// roles by the same display name.</summary>
    public Dictionary<string, string> RoleOf { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TestUser this[string key] =>
        string.Equals(key, "admin", StringComparison.OrdinalIgnoreCase) ? Admin
        : People.TryGetValue(key, out var u) ? u
        : throw new KeyNotFoundException($"Workspace {Key} has no seeded user '{key}'.");
}

public static class World
{
    /// <summary>Unique to this run, e.g. "1014-153012".</summary>
    public static string Stamp { get; } = DateTime.UtcNow.ToString("MMdd-HHmmss");

    public static TestUser SuperAdmin { get; set; } = null!;
    public static TestTenant A { get; set; } = null!;
    public static TestTenant B { get; set; } = null!;

    /// <summary>Null until WorkspaceSettingsTests builds it.</summary>
    public static TestTenant? C { get; set; }

    /// <summary>089. Null until CustomFieldTests or NotificationTests builds it.</summary>
    public static TestTenant? D { get; set; }

    /// <summary>103. Null until WorkspaceTimeTests builds it: India / INR, but on New York time (daylight saving).</summary>
    public static TestTenant? T { get; set; }

    /// <summary>103. Null until EntitlementTests builds it: India / INR, an admin and one rep — its limits and trial are changed by that test.</summary>
    public static TestTenant? E { get; set; }

    /// <summary>109. Null until MarketTests builds it: Philippines / PHP / Asia/Manila, admin only.</summary>
    public static TestTenant? PH { get; set; }

    /// <summary>109. Null until MarketTests builds it: UAE / AED / Asia/Dubai, admin only.</summary>
    public static TestTenant? AE { get; set; }

    /// <summary>A unique address under a per-run domain. example.com is
    /// reserved, so even a misrouted email can never reach a real person.</summary>
    public static string EmailFor(string tenantKey, string who) =>
        $"{who}@{tenantKey.ToLowerInvariant()}{Stamp.Replace("-", "")}.example.com";

    public static string Unique(string label) => $"{label} {Stamp}-{Random.Shared.Next(100, 999)}";
}
