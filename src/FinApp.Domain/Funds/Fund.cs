using FinApp.Domain.Common;

namespace FinApp.Domain.Funds;

/// <summary>
/// A place money physically lives (Bank, Cash, a digital wallet…). Account-level and user-managed:
/// stored flat on the <c>Account</c> and referenced by id from expenses, opening balances and transfers
/// — the same pattern as budget categories. Replaces the old fixed <c>FundType</c> enum.
///
/// Funds are flat. An optional free-text <see cref="Note"/> can describe a fund. <see cref="ParentId"/> is
/// vestigial (sub-funds were removed) and retained only so older persisted snapshots keep deserializing.
/// </summary>
public sealed class Fund : Entity
{
    public string Name { get; private set; }

    /// <summary>Vestigial: sub-funds were removed. Always null for funds created now.</summary>
    public Guid? ParentId { get; private set; }

    /// <summary>Optional free-text note describing the fund.</summary>
    public string? Note { get; private set; }

    public Fund(string name, Guid? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Fund name is required.", nameof(name));
        Name = name.Trim();
        ParentId = parentId;
    }

    public bool IsRoot => ParentId is null;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Fund name is required.", nameof(name));
        Name = name.Trim();
    }

    public void SetNote(string? note) => Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}
