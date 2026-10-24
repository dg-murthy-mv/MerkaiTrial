// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/StageChangeTests.cs
//
// TEST 88 (round 102) —
//   Changing_a_pipeline_stage_preserves_deal_history_and_permissions
//
// Workspaces design their own sales process, so this test does not assume
// one: it asks the deal which moves it offers (GET …/transitions) and
// picks its moves from that answer.
//
//   • An allowed move is made, and the history records it once — from,
//     to, and WHO (the person's name) — with an audit row.
//   • A stage that doesn't exist is refused; nothing is recorded.
//   • The second route, PATCH …/stage, follows the same rules (round 102):
//     a move the process refuses — one that needs a note, is blocked, or
//     isn't part of the process — is refused there too, and an allowed one
//     records the PERSON, never "System". Before 102 this route was the
//     automatic "system" move: it skipped the rules and wrote "System".
//   • A viewer can't move the deal (403); another workspace can't move it,
//     see its moves or read its history.
//   • Two moves at the same moment both land in order: the history is one
//     unbroken chain (each move starts where the previous one ended) and
//     the deal ends where the last move says.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class StageChangeTests : E2ETest
{
    private sealed record Move(string Key, string Name, string Category, bool RequiresNote, bool IsAllowed);

    [Test]
    public async Task Changing_a_pipeline_stage_preserves_deal_history_and_permissions()
    {
        var a = World.A;
        var admin = await Api.AsAsync(a.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var viewer = await Api.AsAsync(a["viewer"]);
        var otherWorkspace = await Api.AsAsync(World.B.Admin);

        var page = await SignedInAsync(a.Admin);
        var dealTitle = World.Unique("Stage history deal");
        var (_, dealId) = await Step("Make a deal", () =>
            Flows.LeadToDealAsync(page, a, World.Unique("Sana Stages"), World.EmailFor("A", "stages" + Random.Shared.Next(1000, 9999)),
                dealTitle, 75000m));

        var historyAtStart = (await Db.StageHistoryAsync(dealId)).Count;
        var auditAtStart = await Db.CountAuditAsync(dealId, "DealStageChanged");

        // ── An allowed move ──────────────────────────────────────────
        var first = await Step("The deal offers at least one move that needs no note", async () =>
        {
            var moves = await MovesAsync(admin, dealId);
            return moves.FirstOrDefault(m => m.IsAllowed && !m.RequiresNote && m.Category == "Open")
                   ?? moves.FirstOrDefault(m => m.IsAllowed && !m.RequiresNote)
                   ?? throw new AssertionException("The deal offers no move that needs no note: " +
                                                   string.Join(", ", moves.Select(m => $"{m.Name} ({(m.IsAllowed ? "allowed" : "blocked")}{(m.RequiresNote ? ", note" : "")})")));
        });

        var fromStage = await Db.DealStageAsync(dealId);
        await Step($"Move to \"{first.Name}\": recorded once, with who made it, and audited", async () =>
        {
            var r = await admin.PutAsync($"/api/deals/{dealId}/stage", new { stage = first.Key });
            Assert.That(r.IsSuccess, Is.True, $"The allowed move failed: {r}");
            Assert.That(await Db.DealStageAsync(dealId), Is.EqualTo(first.Key));

            var history = await Db.StageHistoryAsync(dealId);
            Assert.That(history.Count, Is.EqualTo(historyAtStart + 1), "The move was not recorded exactly once.");
            var last = history[^1];
            Assert.That(last.From, Is.EqualTo(fromStage), "The history row has the wrong 'from' stage.");
            Assert.That(last.To, Is.EqualTo(first.Key), "The history row has the wrong 'to' stage.");
            ExpectPerson(last.By, a.Admin);
            Assert.That(await Db.CountAuditAsync(dealId, "DealStageChanged"), Is.EqualTo(auditAtStart + 1), "The move is not in the audit log.");
        });

        // ── Refusals record nothing ──────────────────────────────────
        await Step("A stage that doesn't exist is refused, and nothing is recorded", async () =>
        {
            var before = (await Db.StageHistoryAsync(dealId)).Count;
            var r = await admin.PutAsync($"/api/deals/{dealId}/stage", new { stage = "no-such-stage-" + World.Stamp });
            Assert.That(r.Status, Is.EqualTo(400), $"A made-up stage was not refused: {r}");
            Assert.That((await Db.StageHistoryAsync(dealId)).Count, Is.EqualTo(before), "A refused move was recorded.");
            Assert.That(await Db.DealStageAsync(dealId), Is.EqualTo(first.Key), "A refused move changed the stage.");
        });

        await Step("PATCH …/stage applies the same rules as the deal page", async () =>
        {
            var moves = await MovesAsync(admin, dealId);
            var stages = await AllStagesAsync(admin, dealId);
            var current = await Db.DealStageAsync(dealId);
            var offered = moves.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The strongest refusal the process offers, in this order.
            var (target, why) =
                moves.FirstOrDefault(m => m.RequiresNote) is { } noted ? (noted.Key, $"\"{noted.Name}\" needs a note")
                : moves.FirstOrDefault(m => !m.IsAllowed) is { } blocked ? (blocked.Key, $"\"{blocked.Name}\" is blocked")
                : stages.FirstOrDefault(s => !offered.Contains(s) && !string.Equals(s, current, StringComparison.OrdinalIgnoreCase)) is { } outside
                    ? (outside, $"\"{outside}\" is not a move from here")
                : (null, null);

            if (target is null)
            {
                TestContext.Progress.WriteLine("    (this workspace's process allows every move without a note — nothing for PATCH to refuse)");
                return;
            }

            var before = (await Db.StageHistoryAsync(dealId)).Count;
            var r = await admin.PatchAsync($"/api/deals/{dealId}/stage", new { stage = target, probability = 100 });
            Assert.That(r.Status, Is.InRange(400, 403),
                $"PATCH made a move the process refuses ({why}): {r}. It must apply the same rules as the deal page.");
            Assert.That(await Db.DealStageAsync(dealId), Is.EqualTo(current), $"PATCH moved the deal although {why}.");
            Assert.That((await Db.StageHistoryAsync(dealId)).Count, Is.EqualTo(before), "A refused PATCH was recorded.");
        });

        await Step("An allowed PATCH records the person, not \"System\"", async () =>
        {
            var next = (await MovesAsync(admin, dealId)).FirstOrDefault(m => m.IsAllowed && !m.RequiresNote);
            if (next is null)
            {
                TestContext.Progress.WriteLine("    (no move without a note from here — skipped)");
                return;
            }

            var r = await admin.PatchAsync($"/api/deals/{dealId}/stage", new { stage = next.Key, probability = 0 });
            Assert.That(r.IsSuccess, Is.True, $"An allowed move through PATCH failed: {r}");
            var last = (await Db.StageHistoryAsync(dealId))[^1];
            Assert.That(last.To, Is.EqualTo(next.Key));
            ExpectPerson(last.By, a.Admin);
        });

        // ── Permissions ──────────────────────────────────────────────
        await Step("A viewer can't move the deal; another workspace can't move it, see its moves or read its history", async () =>
        {
            var before = (await Db.StageHistoryAsync(dealId)).Count;
            var stage = await Db.DealStageAsync(dealId);
            var anyKey = (await MovesAsync(admin, dealId)).FirstOrDefault()?.Key ?? first.Key;

            var v = await viewer.PutAsync($"/api/deals/{dealId}/stage", new { stage = anyKey });
            Assert.That(v.Status, Is.EqualTo(403), $"A viewer could move the deal: {v}");
            var vp = await viewer.PatchAsync($"/api/deals/{dealId}/stage", new { stage = anyKey, probability = 0 });
            Assert.That(vp.Status, Is.EqualTo(403), $"A viewer could move the deal through PATCH: {vp}");

            var o = await otherWorkspace.PutAsync($"/api/deals/{dealId}/stage", new { stage = anyKey });
            Assert.That(o.Status, Is.EqualTo(404), $"Another workspace could move the deal: {o}");
            var ot = await otherWorkspace.GetAsync($"/api/deals/{dealId}/transitions");
            Assert.That(ot.Status, Is.EqualTo(404), $"Another workspace could see the deal's moves: {ot}");
            var oh = await otherWorkspace.GetAsync($"/api/deals/{dealId}/stage-history");
            Assert.That(oh.IsSuccess ? oh.Json().GetArrayLength() : 0, Is.EqualTo(0),
                $"Another workspace could read the deal's history: {oh}");

            Assert.That((await Db.StageHistoryAsync(dealId)).Count, Is.EqualTo(before), "A refused move was recorded.");
            Assert.That(await Db.DealStageAsync(dealId), Is.EqualTo(stage), "A refused move changed the stage.");
        });

        // ── Two at once ──────────────────────────────────────────────
        await Step("Two moves at the same moment: the history stays one unbroken chain", async () =>
        {
            var options = (await MovesAsync(admin, dealId)).Where(m => m.IsAllowed && !m.RequiresNote).Take(2).ToList();
            if (options.Count == 0)
            {
                TestContext.Progress.WriteLine("    (no move without a note from here — skipped)");
                return;
            }
            var t1 = options[0];
            var t2 = options.Count > 1 ? options[1] : options[0];

            var before = (await Db.StageHistoryAsync(dealId)).Count;
            var answers = await Api.AllAtOnceAsync(
                () => admin.PutAsync($"/api/deals/{dealId}/stage", new { stage = t1.Key }),
                () => admin.PutAsync($"/api/deals/{dealId}/stage", new { stage = t2.Key }));
            TestContext.Progress.WriteLine($"    the two moves answered {answers[0].Status}, {answers[1].Status}");

            Assert.That(answers.All(x => x.Status < 500), Is.True, $"A move failed with a server error: {answers[0]} / {answers[1]}");
            var history = await Db.StageHistoryAsync(dealId);
            Assert.That(history.Count - before, Is.LessThanOrEqualTo(answers.Count(x => x.IsSuccess)),
                "More history rows were written than moves succeeded.");
            Assert.That(await Db.DealStageAsync(dealId), Is.EqualTo(history[^1].To),
                "The deal's stage is not where its last history row says.");
        });

        await Step("The whole history is one chain — each move starts where the last one ended — and the API shows all of it", async () =>
        {
            var history = await Db.StageHistoryAsync(dealId);
            for (var i = historyAtStart + 1; i < history.Count; i++)
                Assert.That(history[i].From, Is.EqualTo(history[i - 1].To),
                    $"History row {i + 1} starts at \"{history[i].From}\" but the row before ended at \"{history[i - 1].To}\".");

            var api = await admin.GetAsync($"/api/deals/{dealId}/stage-history");
            Assert.That(api.IsSuccess, Is.True, $"Reading the history failed: {api}");
            Assert.That(api.Json().GetArrayLength(), Is.EqualTo(history.Count), "The API's history and the database's disagree.");
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static async Task<List<Move>> MovesAsync(ApiCaller who, Guid dealId)
    {
        var r = await who.GetAsync($"/api/deals/{dealId}/transitions");
        Assert.That(r.IsSuccess, Is.True, $"Reading the deal's moves failed: {r}");
        return r.Json().GetProperty("transitions").EnumerateArray().Select(t => new Move(
            t.GetProperty("toStageKey").GetString() ?? "",
            t.GetProperty("toStageName").GetString() ?? "",
            t.GetProperty("toCategory").ValueKind == JsonValueKind.String ? t.GetProperty("toCategory").GetString() ?? "" : t.GetProperty("toCategory").ToString(),
            t.GetProperty("requiresNote").GetBoolean(),
            t.GetProperty("isAllowed").GetBoolean())).ToList();
    }

    /// <summary>Every active stage key (sent to workspace admins only).</summary>
    private static async Task<List<string>> AllStagesAsync(ApiCaller admin, Guid dealId)
    {
        var r = await admin.GetAsync($"/api/deals/{dealId}/transitions");
        if (!r.IsSuccess || !r.Json().TryGetProperty("allStages", out var all) || all.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return all.EnumerateArray()
            .Where(s => !s.TryGetProperty("isActive", out var a) || a.GetBoolean())
            .Select(s => s.GetProperty("key").GetString() ?? "")
            .Where(k => k.Length > 0)
            .ToList();
    }

    private static void ExpectPerson(string? by, TestUser who)
    {
        Assert.That(by, Is.Not.Null.And.Not.Empty, "The history row doesn't say who made the move.");
        Assert.That(by, Is.Not.EqualTo("System"), "The history says \"System\" for a move a person made.");
        Assert.That(by, Does.Contain(who.FirstName), $"The history names \"{by}\", not {who.FullName}.");
    }
}
