using System.Security.Cryptography;
using System.Text;

namespace LinkForge;

/// <summary>Tamper-evident audit chain: each event's hash covers the previous event's hash.</summary>
public static class AuditLedger
{
    public const string Genesis = "";

    public static string ComputeHash(string previousHash, int sequence, DateTimeOffset timestamp, string runId,
        int requirementVersion, string eventName, string details)
    {
        var input = $"{previousHash}|{sequence}|{timestamp.UtcTicks}|{runId}|{requirementVersion}|{eventName}|{details}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public static (bool Valid, int? BrokenAtSequence) Verify(IEnumerable<WorkflowAuditEntity> events)
    {
        var previous = Genesis;
        var expectedSequence = 1;
        foreach (var item in events.OrderBy(entry => entry.Sequence))
        {
            var hash = ComputeHash(previous, item.Sequence, item.Timestamp, item.RunId,
                item.RequirementVersion, item.Event, item.Details);
            if (item.Sequence != expectedSequence || item.PreviousHash != previous || item.Hash != hash)
                return (false, item.Sequence);
            previous = item.Hash;
            expectedSequence++;
        }
        return (true, null);
    }
}
