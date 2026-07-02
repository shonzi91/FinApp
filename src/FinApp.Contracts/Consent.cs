namespace FinApp.Contracts;

/// <summary>Current consent state for a scope. <see cref="Active"/> = latest event is a grant under the policy
/// version currently in force; <see cref="At"/> is when it was last (re)granted.</summary>
public record ConsentStatusDto(string Scope, bool Active, DateTimeOffset? At, string PolicyVersion);

/// <summary>Record a consent grant (or withdrawal) for a scope, optionally against an account.</summary>
public record RecordConsentRequest(string Scope, Guid? AccountId, bool Granted);
