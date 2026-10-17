// =====================================================================
// LeadMergeHandlers.cs
// Location: MerkaiTrial.Application/Commands/Leads/LeadMergeHandlers.cs
//
// NEW FILE (093). Finding duplicate leads, and merging two into one.
//
// ─────────────────────────────────────────────────────────────────────
// WHAT COUNTS AS A DUPLICATE
//
//   • the same EMAIL (any capitals);
//   • the same PHONE NUMBER, compared as digits in international form —
//     "081-234-5678", "+66 81 234 5678" and "0812345678" are one number
//     in a Thai workspace (LeadFileParser.NormalisePhone, the importer's
//     own rule, with the workspace's country);
//   • the same NAME at the same COMPANY (any capitals).
//
//   Only leads the person can SEE are compared. A rep with "Own"
//   visibility is never told "a lead with this email already exists" about
//   a colleague's lead — that sentence alone would leak that the customer
//   is someone else's.
//
// ─────────────────────────────────────────────────────────────────────
// WHAT A MERGE DOES (one transaction — all of it or none of it)
//
//   1. The KEPT lead takes the other lead's value for every field the
//      person picked (MergeLeadsDto.TakeFromOther). Everything else keeps
//      the kept lead's value.
//   2. Custom fields the same way, field by field ("cf_" + id).
//   3. EVERYTHING ATTACHED to the other lead moves to the kept one:
//        activities and tasks       Activities (EntityType "Lead")
//        notes, reminders, and the old activity log
//                                   LeadNotes, LeadReminders, LeadActivities
//        files                      Attachments (EntityType "Lead")
//        emails received / filed    InboundEmails.LeadId
//        emails sent about it       OutboundMessages (EntityType "Lead")
//        reply addresses            EmailReplyTokens (EntityType "Lead"),
//                                   so a customer replying to an old email
//                                   lands on the kept lead
//        notifications              Notifications (EntityType "Lead"), so
//                                   an old bell link opens the kept lead
//   4. The other lead is deleted (soft, like every lead delete).
//   5. The kept lead gets an activity "Merged with …" saying what moved,
//      so the merge is on its own Activities tab and timeline; both leads
//      get a LeadMerged audit row.
//
//   NOT MOVED, ON PURPOSE: the other lead's AUDIT HISTORY. An audit log is
//   a record of what happened to that row; rewriting it to point elsewhere
//   would make it say things that never happened. It stays on the deleted
//   lead, and the LeadMerged rows link the two.
//
//   REFUSED: a CONVERTED lead, on either side. Its deal, contact and
//   company point at it, and a merge would have to re-point a deal's
//   history — that is a decision about the deal, not a tidy-up of leads.
//   REFUSED: a lead the person cannot see (404, as everywhere).
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Commands.Leads.Import;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== DTOs ====================

    /// <summary>What to look for. Any part may be empty; at least one must not be.</summary>
    public record LeadDuplicateQuery(
        Guid TenantId,
        string? Email,
        string? Phone,
        string? FullName,
        string? CompanyName,
        Guid? ExcludeLeadId = null);

    /// <summary>One lead that looks like the same person.</summary>
    public class LeadDuplicateDto
    {
        public Guid Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? CompanyName { get; set; }
        public string StatusName { get; set; } = string.Empty;
        public string? OwnerName { get; set; }
        public bool IsConverted { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>"Same email", "Same phone number", "Same name and company".</summary>
        public List<string> Reasons { get; set; } = new();
    }

    public class MergeLeadsDto
    {
        public Guid KeepLeadId { get; set; }
        public Guid MergeLeadId { get; set; }

        /// <summary>
        /// Field keys (LeadMergeFields, or "cf_" + a custom field id) where
        /// the OTHER lead's value wins. Everything not listed keeps the kept
        /// lead's value — which is what an empty list means.
        /// </summary>
        public List<string> TakeFromOther { get; set; } = new();
    }

    public class MergeLeadsResultDto
    {
        public Guid KeptLeadId { get; set; }
        public int ItemsMoved { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>The standard fields a merge can take from the other lead.</summary>
    public static class LeadMergeFields
    {
        public const string FullName       = "fullName";
        public const string Email          = "email";
        public const string Phone          = "phone";
        public const string CompanyName    = "companyName";
        public const string Address        = "address";
        public const string Country        = "country";
        public const string Currency       = "currency";
        public const string Channel        = "channel";
        public const string Source         = "source";
        public const string Industry       = "industry";
        public const string Status         = "status";
        public const string EstimatedValue = "estimatedValue";
        public const string Owner          = "owner";

        /// <summary>In the order the merge screen shows them.</summary>
        public static readonly IReadOnlyList<(string Key, string Label)> All = new[]
        {
            (FullName, "Full name"), (Email, "Email"), (Phone, "Phone"), (CompanyName, "Company"),
            (Address, "Address"), (Country, "Country"), (Currency, "Currency"), (Channel, "Channel"),
            (Source, "Lead source"), (Industry, "Industry"), (Status, "Status"),
            (EstimatedValue, "Estimated value"), (Owner, "Owner")
        };

        public static string LabelOf(string key) => All.FirstOrDefault(f => f.Key == key).Label ?? key;
    }

    // ==================== FIND DUPLICATES ====================

    public class FindLeadDuplicatesHandler : ICommandHandler
    {
        /// <summary>More than this is a data-quality report, not a warning.</summary>
        public const int MaxResults = 5;

        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ILeadStatusResolver _statuses;
        private readonly ICurrentTenantService _tenant;

        public FindLeadDuplicatesHandler(
            FlowDbContext db, IRecordScopeService scope, ILeadStatusResolver statuses, ICurrentTenantService tenant)
        {
            _db = db;
            _scope = scope;
            _statuses = statuses;
            _tenant = tenant;
        }

        public async Task<List<LeadDuplicateDto>> Handle(LeadDuplicateQuery query, CancellationToken ct = default)
        {
            var email   = Clean(query.Email);
            var name    = Clean(query.FullName);
            var company = Clean(query.CompanyName);
            var phoneKey = LeadFileParser.NormalisePhone(query.Phone, SafeCountryCode());

            if (email is null && phoneKey is null && (name is null || company is null))
                return new List<LeadDuplicateDto>();

            // Visible leads only — see the header.
            var access = await _scope.GetAsync(RecordModules.Leads, ct);
            var leads = _db.Leads.AsNoTracking()
                .Where(l => l.TenantId == query.TenantId && !l.IsDeleted)
                .VisibleTo(access);

            if (query.ExcludeLeadId is { } exclude)
                leads = leads.Where(l => l.Id != exclude);

            // Lead id → reasons, in the order found (email first: the strongest).
            var reasons = new Dictionary<Guid, List<string>>();
            void Add(Guid id, string why)
            {
                if (!reasons.TryGetValue(id, out var list)) reasons[id] = list = new List<string>();
                if (!list.Contains(why)) list.Add(why);
            }

            if (email is not null)
            {
                // SQL Server's default collation compares case-insensitively;
                // ToLower on both sides keeps that true on a case-sensitive one.
                var lower = email.ToLower();
                foreach (var id in await leads.Where(l => l.Email != null && l.Email.ToLower() == lower)
                                              .Select(l => l.Id).Take(MaxResults).ToListAsync(ct))
                    Add(id, "Same email");
            }

            if (phoneKey is not null)
            {
                // Numbers are stored as typed ("081-234-5678"), so they are
                // compared after normalising, here. The query is narrowed
                // first by the last two digits, which every way of writing
                // a number keeps side by side ("…56 78" would not — so two,
                // not four).
                var digits = new string(phoneKey.Where(char.IsDigit).ToArray());
                var tail = digits.Length > 2 ? digits[^2..] : digits;
                var cc = SafeCountryCode();

                var candidates = await leads
                    .Where(l => l.Phone != null && l.Phone.Contains(tail))
                    .Select(l => new { l.Id, l.Phone })
                    .Take(5000)
                    .ToListAsync(ct);

                foreach (var c in candidates)
                    if (LeadFileParser.NormalisePhone(c.Phone, cc) == phoneKey)
                        Add(c.Id, "Same phone number");
            }

            if (name is not null && company is not null)
            {
                var n = name.ToLower();
                var co = company.ToLower();
                foreach (var id in await leads.Where(l => l.FullName.ToLower() == n &&
                                                          l.CompanyName != null && l.CompanyName.ToLower() == co)
                                              .Select(l => l.Id).Take(MaxResults).ToListAsync(ct))
                    Add(id, "Same name and company");
            }

            if (reasons.Count == 0) return new List<LeadDuplicateDto>();

            var ids = reasons.Keys.Take(MaxResults).ToList();
            var rows = await _db.Leads.AsNoTracking()
                .Where(l => ids.Contains(l.Id))
                .Select(l => new
                {
                    l.Id, l.FullName, l.Email, l.Phone, l.CompanyName, l.Status, l.OwnerUserId,
                    l.IsConverted, l.CreatedAtUtc
                })
                .ToListAsync(ct);

            var ownerIds = rows.Select(r => Guid.TryParse(r.OwnerUserId, out var g) ? g : Guid.Empty)
                               .Where(g => g != Guid.Empty).Distinct().ToList();
            var owners = (await _db.Users.AsNoTracking()
                    .Where(u => u.TenantId == query.TenantId && ownerIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.FullName })
                    .ToListAsync(ct))
                .ToDictionary(u => u.Id.ToString(), u => u.FullName, StringComparer.OrdinalIgnoreCase);

            var statuses = await _statuses.GetAsync(query.TenantId, ct);

            return ids
                .Select(id => rows.FirstOrDefault(r => r.Id == id))
                .Where(r => r is not null)
                .Select(r => new LeadDuplicateDto
                {
                    Id = r!.Id,
                    FullName = r.FullName,
                    Email = r.Email,
                    Phone = r.Phone,
                    CompanyName = r.CompanyName,
                    StatusName = statuses.NameOf(r.Status),
                    OwnerName = r.OwnerUserId is not null && owners.TryGetValue(r.OwnerUserId, out var on) ? on : null,
                    IsConverted = r.IsConverted,
                    CreatedAtUtc = r.CreatedAtUtc,
                    Reasons = reasons[r.Id]
                })
                .ToList();
        }

        private string? SafeCountryCode()
        {
            try { return _tenant.GetCountryCode(); }
            catch (Exception) { return null; }
        }

        private static string? Clean(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // ==================== MERGE ====================

    public class MergeLeadsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IRecordScopeService _scope;
        private readonly ICurrentUserService _currentUser;
        private readonly ILeadScoringService _scoring;
        private readonly IAuditService _audit;
        private readonly ILogger<MergeLeadsHandler> _logger;

        public MergeLeadsHandler(
            FlowDbContext db,
            IRecordScopeService scope,
            ICurrentUserService currentUser,
            ILeadScoringService scoring,
            IAuditService audit,
            ILogger<MergeLeadsHandler> logger)
        {
            _db = db;
            _scope = scope;
            _currentUser = currentUser;
            _scoring = scoring;
            _audit = audit;
            _logger = logger;
        }

        public async Task<MergeLeadsResultDto> Handle(Guid tenantId, MergeLeadsDto dto, CancellationToken ct = default)
        {
            if (dto.KeepLeadId == Guid.Empty || dto.MergeLeadId == Guid.Empty)
                throw new InvalidOperationException("Choose the two leads to merge.");
            if (dto.KeepLeadId == dto.MergeLeadId)
                throw new InvalidOperationException("A lead can't be merged with itself. Choose a different lead.");

            var access = await _scope.GetAsync(RecordModules.Leads, ct);

            var pair = await _db.Leads
                .Where(l => (l.Id == dto.KeepLeadId || l.Id == dto.MergeLeadId) &&
                            l.TenantId == tenantId && !l.IsDeleted)
                .VisibleTo(access)
                .ToListAsync(ct);

            var keep  = pair.FirstOrDefault(l => l.Id == dto.KeepLeadId)
                        ?? throw new KeyNotFoundException($"Lead {dto.KeepLeadId} not found");
            var other = pair.FirstOrDefault(l => l.Id == dto.MergeLeadId)
                        ?? throw new KeyNotFoundException($"Lead {dto.MergeLeadId} not found");

            if (keep.IsConverted || other.IsConverted)
                throw new InvalidOperationException(
                    $"\"{(keep.IsConverted ? keep.FullName : other.FullName)}\" has been converted to a deal, so it can't be merged. " +
                    "Only leads that are still leads can be merged.");

            var take = new HashSet<string>(dto.TakeFromOther ?? new List<string>(), StringComparer.Ordinal);
            var actorId = _currentUser.GetCurrentUserId().ToString();
            var now = DateTime.UtcNow;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                // ── 1. Standard fields ────────────────────────────────────
                var taken = new List<string>();
                void Take(string key, Action apply)
                {
                    if (!take.Contains(key)) return;
                    apply();
                    taken.Add(LeadMergeFields.LabelOf(key));
                }

                Take(LeadMergeFields.FullName, () => { if (!string.IsNullOrWhiteSpace(other.FullName)) keep.FullName = other.FullName; });
                Take(LeadMergeFields.Email, () => keep.Email = other.Email);
                Take(LeadMergeFields.Phone, () => keep.Phone = other.Phone);
                Take(LeadMergeFields.CompanyName, () => keep.CompanyName = other.CompanyName);
                Take(LeadMergeFields.Address, () => keep.Address = other.Address);
                Take(LeadMergeFields.Country, () => keep.CountryId = other.CountryId);
                Take(LeadMergeFields.Currency, () => keep.Currency = other.Currency);
                Take(LeadMergeFields.Channel, () => { keep.ChannelId = other.ChannelId; keep.Channel = other.Channel; });
                Take(LeadMergeFields.Source, () => { keep.SourceId = other.SourceId; keep.Source = other.Source; });
                Take(LeadMergeFields.Industry, () => keep.VerticalId = other.VerticalId);
                Take(LeadMergeFields.Status, () => keep.Status = other.Status);
                Take(LeadMergeFields.EstimatedValue, () => keep.EstimatedValue = other.EstimatedValue);
                Take(LeadMergeFields.Owner, () => keep.OwnerUserId = other.OwnerUserId);

                // A link the kept lead lacks is filled, never replaced.
                keep.ContactId ??= other.ContactId;
                keep.CompanyId ??= other.CompanyId;

                keep.UpdatedAtUtc = now;
                keep.UpdatedBy = actorId;

                // ── 2. Custom fields ──────────────────────────────────────
                var customTaken = await MergeCustomFieldsAsync(tenantId, keep.Id, other.Id, take, actorId, now, ct);
                taken.AddRange(customTaken);

                // ── 3. Everything attached moves ──────────────────────────
                var moved = await MoveAttachedAsync(tenantId, keep.Id, other.Id, ct);

                // ── 4. The other lead goes ────────────────────────────────
                other.IsDeleted = true;
                other.UpdatedAtUtc = now;
                other.UpdatedBy = actorId;

                // ── 5. Say so on the kept lead ────────────────────────────
                _db.Activities.Add(new Activity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EntityType = "Lead",
                    EntityId = keep.Id,
                    ActivityType = "Note",
                    Subject = Truncate($"Merged with \"{other.FullName}\"", 200),
                    Description = Describe(other, moved, taken),
                    ActivityDate = now,
                    IsTask = false,
                    IsCompleted = false,
                    CreatedAtUtc = now,
                    CreatedBy = actorId
                });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation(
                    "Merged lead {Other} into {Keep} for tenant {TenantId}: {Moved} item(s) moved, {Taken} field(s) taken",
                    other.Id, keep.Id, tenantId, moved.Total, taken.Count);

                // ── After the commit: never undoes the merge ─────────────
                try { await _scoring.RecalculateAsync(keep.Id, tenantId, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Score not recalculated after merging into {Keep}", keep.Id); }

                try
                {
                    await _audit.WriteAsync(AuditAction.LeadMerged, AuditEntityType.Lead, keep.Id, tenantId,
                        new { mergedFrom = other.Id, mergedName = other.FullName, fieldsTaken = taken, moved = moved.ToAudit() }, ct);
                    await _audit.WriteAsync(AuditAction.LeadMerged, AuditEntityType.Lead, other.Id, tenantId,
                        new { mergedInto = keep.Id, keptName = keep.FullName }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Merge of {Other} into {Keep} succeeded but could not be audited", other.Id, keep.Id);
                }

                return new MergeLeadsResultDto
                {
                    KeptLeadId = keep.Id,
                    ItemsMoved = moved.Total,
                    Message = $"\"{other.FullName}\" was merged into \"{keep.FullName}\"." +
                              (moved.Total > 0 ? " " + moved.Sentence() : "")
                };
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        /// <summary>
        /// Per custom field: when the other lead's value is chosen, the kept
        /// lead's row is overwritten with it (or removed, when the other has
        /// none). The other lead's rows are then removed — it is deleted.
        /// Rows are updated in place rather than moved, so the unique
        /// (field, record) pair is never held by two rows at once.
        /// Returns the labels of the fields taken.
        /// </summary>
        private async Task<List<string>> MergeCustomFieldsAsync(
            Guid tenantId, Guid keepId, Guid otherId, ISet<string> take, string actor, DateTime now, CancellationToken ct)
        {
            var taken = new List<string>();

            var defs = await _db.CustomFieldDefinitions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Lead && !d.IsDeleted)
                .ToListAsync(ct);

            var rows = await _db.CustomFieldValues
                .Where(v => v.TenantId == tenantId && (v.EntityId == keepId || v.EntityId == otherId))
                .ToListAsync(ct);

            foreach (var def in defs)
            {
                if (!take.Contains(ImportFieldKeys.ForCustomField(def.Id))) continue;

                var mine = rows.FirstOrDefault(r => r.EntityId == keepId && r.DefinitionId == def.Id);
                var theirs = rows.FirstOrDefault(r => r.EntityId == otherId && r.DefinitionId == def.Id);

                if (theirs is null)
                {
                    if (mine is not null) _db.CustomFieldValues.Remove(mine);
                }
                else if (mine is null)
                {
                    _db.CustomFieldValues.Add(new CustomFieldValue
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        DefinitionId = def.Id,
                        EntityId = keepId,
                        TextValue = theirs.TextValue,
                        NumberValue = theirs.NumberValue,
                        DateValue = theirs.DateValue,
                        BoolValue = theirs.BoolValue,
                        CreatedAtUtc = now,
                        UpdatedBy = actor.Length > 64 ? actor[..64] : actor
                    });
                }
                else
                {
                    mine.TextValue = theirs.TextValue;
                    mine.NumberValue = theirs.NumberValue;
                    mine.DateValue = theirs.DateValue;
                    mine.BoolValue = theirs.BoolValue;
                    mine.UpdatedAtUtc = now;
                    mine.UpdatedBy = actor.Length > 64 ? actor[..64] : actor;
                }

                taken.Add(def.Label);
            }

            foreach (var r in rows.Where(r => r.EntityId == otherId))
                _db.CustomFieldValues.Remove(r);

            return taken;
        }

        /// <summary>Everything that points at the other lead now points at the kept one. Staged; not saved.</summary>
        private async Task<MovedCounts> MoveAttachedAsync(Guid tenantId, Guid keepId, Guid otherId, CancellationToken ct)
        {
            var m = new MovedCounts();

            foreach (var a in await _db.Activities
                         .Where(a => a.TenantId == tenantId && a.EntityType == "Lead" && a.EntityId == otherId)
                         .ToListAsync(ct))
            {
                a.EntityId = keepId;
                if (!a.IsDeleted) m.Activities++;
            }

            foreach (var n in await _db.LeadNotes.Where(n => n.TenantId == tenantId && n.LeadId == otherId).ToListAsync(ct))
            {
                n.LeadId = keepId;
                if (!n.IsDeleted) m.Notes++;
            }

            foreach (var r in await _db.LeadReminders.Where(r => r.TenantId == tenantId && r.LeadId == otherId).ToListAsync(ct))
            {
                r.LeadId = keepId;
                if (!r.IsDeleted) m.Activities++;
            }

            foreach (var la in await _db.LeadActivities.Where(a => a.TenantId == tenantId && a.LeadId == otherId).ToListAsync(ct))
            {
                la.LeadId = keepId;
                if (!la.IsDeleted) m.Activities++;
            }

            foreach (var f in await _db.Attachments
                         .Where(f => f.TenantId == tenantId && f.EntityType == AttachmentEntityType.Lead && f.EntityId == otherId)
                         .ToListAsync(ct))
            {
                f.EntityId = keepId;
                if (!f.IsDeleted) m.Files++;
            }

            foreach (var e in await _db.InboundEmails.Where(e => e.TenantId == tenantId && e.LeadId == otherId).ToListAsync(ct))
            {
                e.LeadId = keepId;
                if (!e.IsDeleted) m.Emails++;
            }

            foreach (var o in await _db.OutboundMessages
                         .Where(o => o.TenantId == tenantId && o.EntityType == "Lead" && o.EntityId == otherId)
                         .ToListAsync(ct))
            {
                o.EntityId = keepId;
                m.Emails++;
            }

            foreach (var t in await _db.EmailReplyTokens
                         .Where(t => t.TenantId == tenantId && t.EntityType == "Lead" && t.EntityId == otherId)
                         .ToListAsync(ct))
                t.EntityId = keepId;

            foreach (var n in await _db.Notifications
                         .Where(n => n.TenantId == tenantId && n.EntityType == "Lead" && n.EntityId == otherId)
                         .ToListAsync(ct))
                n.EntityId = keepId;

            return m;
        }

        private static string Describe(Lead other, MovedCounts moved, List<string> taken)
        {
            var parts = new List<string>();
            var who = string.Join(" · ", new[] { other.Email, other.Phone, other.CompanyName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            parts.Add($"\"{other.FullName}\"{(who.Length > 0 ? $" ({who})" : "")} was merged into this lead and removed.");
            if (moved.Total > 0) parts.Add(moved.Sentence());
            if (taken.Count > 0) parts.Add("Taken from it: " + string.Join(", ", taken) + ".");
            return string.Join(" ", parts);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        private sealed class MovedCounts
        {
            public int Activities { get; set; }
            public int Notes { get; set; }
            public int Files { get; set; }
            public int Emails { get; set; }
            public int Total => Activities + Notes + Files + Emails;

            public object ToAudit() => new { activities = Activities, notes = Notes, files = Files, emails = Emails };

            public string Sentence()
            {
                var bits = new List<string>();
                if (Activities > 0) bits.Add($"{Activities} activit{(Activities == 1 ? "y or task" : "ies and tasks")}");
                if (Notes > 0) bits.Add($"{Notes} note{(Notes == 1 ? "" : "s")}");
                if (Files > 0) bits.Add($"{Files} file{(Files == 1 ? "" : "s")}");
                if (Emails > 0) bits.Add($"{Emails} email{(Emails == 1 ? "" : "s")}");
                return "Moved across: " + string.Join(", ", bits) + ".";
            }
        }
    }
}
