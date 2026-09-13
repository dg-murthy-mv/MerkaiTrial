// =====================================================================
// LeadScoringService.cs
// Location: MerkaiTrial.Application/Services/LeadScoringService.cs
//
// COMPLETE FILE — replaces the existing one.
//
// WHAT CHANGED
//   Engagement now counts the unified Activities table instead of
//   LeadActivities. Since March the Lead page has written to Activities,
//   so every real activity was scoring zero.
//
//   Counted: logged activities, plus tasks that were COMPLETED (a done
//   "Call Khun Nok" task is a call that happened). Open tasks don't count:
//   planning to call someone is not engagement.
//
//   Existing scores correct themselves the next time each lead is touched
//   (edit, note, activity, status change).
//
// Rule-based scoring (Model 2 — same idea as HubSpot/Zoho standard tier).
// Max points: profile 50, notes 15, activities 20, status 15. Ceiling 100.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services;

public interface ILeadScoringService
{
    Task RecalculateAsync(Guid leadId, Guid tenantId, CancellationToken ct = default);
}

public class LeadScoringService : ILeadScoringService
{
    private readonly FlowDbContext               _db;
    private readonly ILogger<LeadScoringService> _logger;

    public LeadScoringService(FlowDbContext db, ILogger<LeadScoringService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task RecalculateAsync(Guid leadId, Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            var lead = await _db.Leads
                .FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId && !l.IsDeleted, ct);

            if (lead == null) return;

            var noteCount = await _db.LeadNotes
                .CountAsync(n => n.LeadId == leadId && n.TenantId == tenantId && !n.IsDeleted, ct);

            var activityCount = await _db.Activities
                .CountAsync(a =>
                    a.TenantId   == tenantId &&
                    a.EntityType == ActivityEntityType.Lead &&
                    a.EntityId   == leadId &&
                    !a.IsDeleted &&
                    (!a.IsTask || a.IsCompleted), ct);

            var newScore = Calculate(lead, noteCount, activityCount);

            if (lead.Score != newScore)
            {
                lead.Score        = newScore;
                lead.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                _logger.LogDebug("Lead {LeadId} score updated to {Score}", leadId, newScore);
            }
        }
        catch (Exception ex)
        {
            // Never throw — scoring failure must not break the main operation
            _logger.LogError(ex, "Failed to recalculate score for lead {LeadId}", leadId);
        }
    }

    // ── Pure calculation — testable with no DB dependency ────────────────

    public static int Calculate(
        MerkaiTrial.Domain.Entities.Lead lead,
        int noteCount,
        int activityCount)
    {
        int score = 0;

        // ── Profile completeness (50 pts) ─────────────────────────────
        if (!string.IsNullOrWhiteSpace(lead.Email))       score += 10;
        if (!string.IsNullOrWhiteSpace(lead.Phone))       score += 10;
        if (!string.IsNullOrWhiteSpace(lead.CompanyName)) score += 10;
        if (lead.CountryId.HasValue)                      score += 5;
        if (lead.VerticalId.HasValue)                     score += 5;
        if (lead.EstimatedValue.GetValueOrDefault() > 0)  score += 5;
        if (lead.ChannelId.HasValue)                      score += 3;
        if (lead.SourceId.HasValue)                       score += 2;

        // ── Engagement (max 35 pts) ───────────────────────────────────
        score += Math.Min(noteCount     * 5, 15);  // 1 note=5, 2=10, 3+=15
        score += Math.Min(activityCount * 5, 20);  // 1 activity=5 ... 4+=20

        // ── Status progression (max 15 pts) ───────────────────────────
        score += lead.Status switch
        {
            LeadStatus.New       => 0,
            LeadStatus.Working   => 5,
            LeadStatus.Qualified => 15,
            LeadStatus.Converted => 15,  // keep score high after conversion
            _                    => 0
        };

        return Math.Min(score, 100);
    }
}
