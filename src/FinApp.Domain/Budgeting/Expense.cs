using FinApp.Domain.Common;
using FinApp.Domain.Periods;

namespace FinApp.Domain.Budgeting;

/// <summary>
/// An immutable ledger entry: money spent in a category, from a fund, by a member, on a date.
/// Append-only — corrections are made by adding a reversing entry, which keeps multi-user
/// sync conflict-free and makes period reconciliation auditable.
/// When <see cref="SourceSavingCategoryId"/> is set, the expense was paid from a savings bucket
/// (a "saving → expense" conversion) and also draws down that saving earmark.
///
/// <para><b>Settlement (on-behalf) links</b> tie an expense paid here to a matching expense in another account:
/// the <i>source</i> side carries <see cref="SettledToAccountId"/> + <see cref="SettledAmount"/> (and its
/// <see cref="Amount"/> is reduced by what was pushed away), while the <i>destination</i> side carries
/// <see cref="SettledFromAccountId"/>. Both share a <see cref="SettlementId"/> so either side can find its
/// counterpart and keep it in step on edit/remove.</para>
/// </summary>
public sealed class Expense : Entity
{
    public Guid CategoryId { get; }
    public Money Amount { get; }
    public DateOnly Date { get; }
    public Guid MemberId { get; }
    public Guid FundId { get; }
    public string? Note { get; }
    public Guid? SourceSavingCategoryId { get; }

    /// <summary>
    /// When true, this expense was paid here but is (partly or wholly) on behalf of another account, so it can
    /// be settled — a chosen amount is pushed onto another account as that account's expense and this expense's
    /// <see cref="Amount"/> is reduced accordingly. Stays flagged even after settling so the action remains available.
    /// </summary>
    public bool OnBehalfOfOtherAccount { get; }

    /// <summary>Shared id linking a source expense to its destination counterpart in another account.</summary>
    public Guid? SettlementId { get; }

    /// <summary>On the <b>source</b> expense: the account a portion of this expense was settled onto (null otherwise).</summary>
    public Guid? SettledToAccountId { get; }

    /// <summary>On the <b>destination</b> expense: the account this expense was settled from (null otherwise).</summary>
    public Guid? SettledFromAccountId { get; }

    /// <summary>On the <b>source</b> expense: how much was pushed onto the other account (already deducted from <see cref="Amount"/>), in the account currency.</summary>
    public decimal SettledAmount { get; }

    public Expense(
        Guid categoryId,
        Money amount,
        DateOnly date,
        Guid memberId,
        Guid fundId,
        string? note = null,
        Guid? sourceSavingCategoryId = null,
        bool onBehalfOfOtherAccount = false,
        Guid? settlementId = null,
        Guid? settledToAccountId = null,
        Guid? settledFromAccountId = null,
        decimal settledAmount = 0m)
    {
        if (amount.IsNegative)
            throw new ArgumentException("Expense amount cannot be negative.", nameof(amount));
        CategoryId = categoryId;
        Amount = amount;
        Date = date;
        MemberId = memberId;
        FundId = fundId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        SourceSavingCategoryId = sourceSavingCategoryId;
        OnBehalfOfOtherAccount = onBehalfOfOtherAccount;
        SettlementId = settlementId;
        SettledToAccountId = settledToAccountId;
        SettledFromAccountId = settledFromAccountId;
        SettledAmount = settledAmount;
    }

    public bool IsFromSavings => SourceSavingCategoryId is not null;

    /// <summary>The settled amount as <see cref="Money"/> (in this expense's currency).</summary>
    public Money SettledMoney => new(SettledAmount, Amount.Currency);

    /// <summary>This expense had a portion settled onto another account (its amount is the reduced, after-settlement value).</summary>
    public bool IsSettlementSource => SettledToAccountId is not null && SettledAmount != 0m;

    /// <summary>This expense was created by settling a portion of an expense in another account.</summary>
    public bool IsSettlementDestination => SettledFromAccountId is not null;

    /// <summary>The expense's value before any settlement was pushed away (= <see cref="Amount"/> + <see cref="SettledAmount"/>).</summary>
    public Money OriginalAmount => Amount + SettledMoney;
}
