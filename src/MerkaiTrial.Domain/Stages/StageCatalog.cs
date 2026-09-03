// src/MerkaiTrial.Domain/Stages/StageCatalog.cs
using System.Collections.Immutable;
using MerkaiTrial.Domain.Enums;

public static class StageCatalog
{
    public static readonly string[] Generic = { "New", "Qualified", "Quote Sent", "Accepted", "Won", "Lost" };
    public static readonly string[] Finance = { "New", "Qualified", "Offer Sent", "Accepted", "Submitted to Bank", "Sanctioned", "Disbursed", "Won", "Lost" };
    public static readonly string[] Logistics = { "New", "Qualified", "Quote Sent", "Booked", "In Transit", "Delivered", "Invoiced", "Won", "Lost" };

    public static IReadOnlyList<string> For(VerticalKind v) => v switch
    {
        VerticalKind.Finance => Finance,
        VerticalKind.Logistics => Logistics,
        _ => Generic
    };

    public static ISet<string> Allowed(VerticalKind v) => For(v).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
}
