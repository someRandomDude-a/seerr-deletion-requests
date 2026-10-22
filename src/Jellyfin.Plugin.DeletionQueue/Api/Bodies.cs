using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using DeletionQueue.Core;

namespace Jellyfin.Plugin.DeletionQueue.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposeBody(Guid CommandId, [Required] TargetSelection Selection,
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] string Fingerprint,
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] string Snapshot);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsentBody(Guid CommandId, [Range(0, long.MaxValue)] long Revision,
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] string Snapshot, Answer Answer);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevisionBody(Guid CommandId, [Range(0, long.MaxValue)] long Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreferencesBody(Guid CommandId, [Range(0, long.MaxValue)] long Revision,
    [Range(48, 8760)] int DelayHours, bool Notifications);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PolicyBody(Guid CommandId, long Revision, [Required] Policy Policy) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Revision < 0 || Policy is null || Policy.Accounts is null || Policy.Accounts.Count > 1000 ||
            Policy.Accounts.Any(a => a.Key == Guid.Empty || !Enum.IsDefined(a.Value)) || !Enum.IsDefined(Policy.DefaultAuthority) ||
            Policy.ActivityDays is < 1 or > 365 || Policy.DelayHours is < 48 or > 8760)
            yield return new ValidationResult("Invalid or excessive policy.");
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntegrationBody(Guid CommandId, [Range(0, long.MaxValue)] long Revision,
    [Required, StringLength(2048)] string BaseUrl, [StringLength(512)] string? ApiKey);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NoticeReadBody(Guid CommandId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdministratorApprovalBody(Guid CommandId, [Range(0, long.MaxValue)] long Revision,
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] string Snapshot, bool Approved);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserResetBody(Guid CommandId, [Range(0, long.MaxValue)] long Revision,
    [Range(0, long.MaxValue)] long PreferencesRevision);
