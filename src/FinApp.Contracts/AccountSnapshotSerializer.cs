using System.Reflection;
using System.Text.Json;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;

namespace FinApp.Contracts;

/// <summary>
/// Serializes a full <see cref="Account"/> aggregate to/from JSON, <b>preserving every entity id</b> so
/// the aggregate's internal references (category/fund/member/saving links) survive a round-trip. Used to
/// move a shared account between client and server as a single <c>AccountSnapshot.Payload</c> string —
/// opaque to the server, so it can later be swapped for an end-to-end-encrypted blob without API changes.
///
/// Entities are rebuilt through their normal constructors (so invariants hold for the simple fields) and a
/// tiny reflection helper restores the bits constructors don't take: the <see cref="Entity.Id"/>, a closed
/// period's status / carried-in amount, and the private child collections.
///
/// Lives in <c>FinApp.Contracts</c> (Domain-only deps, no EF/SQLite) so both the SQLite-backed MAUI host and
/// the SQLite-free Blazor WASM host can use it.
/// </summary>
public static class AccountSnapshotSerializer
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Serialize(Account account)
    {
        var node = new AccountNode(
            account.Id, account.Name, account.Currency, account.OwnerUserId,
            account.Members.Select(m => new MemberNode(m.Id, m.UserId, m.DisplayName)).ToList(),
            account.Funds.Select(f => new FundNode(f.Id, f.Name, f.ParentId, f.Note)).ToList(),
            account.Categories.Select(c => new CategoryNode(c.Id, c.Name, c.ParentId)).ToList(),
            account.SavingCategories.Select(s => new SavingCategoryNode(s.Id, s.Name, s.ParentId, s.GoalAmount, s.AlertThreshold, s.NotifyOnMilestone, s.InitialAmount)).ToList(),
            account.Periods.Select(ToNode).ToList(),
            account.ContributionCategories.Select(c => new ContributionCategoryNode(c.Id, c.Name)).ToList());
        return JsonSerializer.Serialize(node, Json);
    }

    /// <summary>
    /// Build an empty-bodied <see cref="Account"/> from server header data (id/name/currency/owner/members),
    /// for a freshly-created account that has no snapshot yet. The caller seeds the body and saves.
    /// </summary>
    public static Account CreateForHeader(Guid id, string name, string currency, Guid ownerUserId,
        IEnumerable<(Guid UserId, string DisplayName)> members)
    {
        var account = new Account(name, currency);
        SetId(account, id);
        SetAuto(account, nameof(Account.OwnerUserId), ownerUserId);
        SetField(account, "_members", members.Select(m => new AccountMember(m.UserId, m.DisplayName)).ToList());
        return account;
    }

    public static Account Deserialize(string payload)
    {
        var node = JsonSerializer.Deserialize<AccountNode>(payload, Json)
                   ?? throw new ArgumentException("Snapshot payload is empty.", nameof(payload));

        var account = new Account(node.Name, node.Currency);
        SetId(account, node.Id);
        SetAuto(account, nameof(Account.OwnerUserId), node.OwnerUserId);
        SetField(account, "_members", node.Members.Select(m => Build(new AccountMember(m.UserId, m.DisplayName), m.Id)).ToList());
        SetField(account, "_funds", node.Funds.Select(f =>
        {
            var fund = Build(new Fund(f.Name, f.ParentId), f.Id);
            fund.SetNote(f.Note);
            return fund;
        }).ToList());
        SetField(account, "_categories", node.Categories.Select(c => Build(new Category(c.Name, c.ParentId), c.Id)).ToList());
        SetField(account, "_savingCategories", node.SavingCategories.Select(ToEntity).ToList());
        SetField(account, "_contributionCategories",
            (node.ContributionCategories ?? []).Select(c => Build(new ContributionCategory(c.Name), c.Id)).ToList());
        SetField(account, "_periods", node.Periods.Select(p => ToEntity(p, node.Currency)).ToList());
        return account;
    }

    // --- domain -> node ---------------------------------------------------

    private static PeriodNode ToNode(Period p) => new(
        p.Id, p.Currency, p.From, p.To, p.Status, p.CarriedIn.Amount,
        p.InitialBalances.Select(b => new InitialBalanceNode(b.Id, b.FundId, b.Amount.Amount, b.Informative)).ToList(),
        p.Contributions.Select(c => new ContributionNode(c.Id, c.MemberId, c.Paid.Amount, c.CategoryId, c.FundId, c.Date)).ToList(),
        p.Budgets.Select(b => new BudgetNode(b.Id, b.CategoryId, b.Allocated.Amount, b.AlertThreshold, b.NotifyOnEveryExpense)).ToList(),
        p.Expenses.Select(e => new ExpenseNode(e.Id, e.CategoryId, e.Amount.Amount, e.Date, e.MemberId, e.FundId, e.Note, e.SourceSavingCategoryId)).ToList(),
        p.SavingAllocations.Select(a => new SavingAllocationNode(a.Id, a.SavingCategoryId, a.Amount.Amount, a.Date, a.Note, a.SourceExpenseId, a.BudgetCategoryId, a.TransferPairId)).ToList(),
        p.FundTransfers.Select(t => new FundTransferNode(t.Id, t.FromFundId, t.ToFundId, t.Amount.Amount, t.Date, t.Note)).ToList(),
        p.ExternalTransfers.Select(t => new ExternalTransferNode(t.Id, t.FundId, t.Amount.Amount, t.Date, t.ToAccountId, t.Note)).ToList());

    // --- node -> domain ---------------------------------------------------

    private static SavingCategory ToEntity(SavingCategoryNode n)
    {
        var s = Build(new SavingCategory(n.Name, n.ParentId), n.Id);
        s.SetGoal(n.GoalAmount, n.AlertThreshold, n.NotifyOnMilestone);
        if (n.InitialAmount != 0m) s.SetInitialAmount(n.InitialAmount);
        return s;
    }

    private static Period ToEntity(PeriodNode n, string currency)
    {
        Money M(decimal v) => new(v, currency);
        var p = Build(new Period(n.Currency, n.From, n.To), n.Id);

        // Carryover now lives signed in CarriedIn. Older snapshots stored it as a CarryoverSource contribution —
        // fold that into CarriedIn and keep it out of the contributions list so it isn't counted twice.
        var legacyCarryover = n.Contributions.FirstOrDefault(c => c.MemberId == Period.CarryoverSource)?.Paid ?? 0m;
        var carriedIn = n.CarriedIn != 0m ? n.CarriedIn : legacyCarryover;
        if (carriedIn != 0m) SetAuto(p, nameof(Period.CarriedIn), M(carriedIn));
        if (n.Status == PeriodStatus.Closed) p.Close();

        SetField(p, "_initialBalances", n.InitialBalances.Select(b => Build(new InitialBalance(b.FundId, M(b.Amount), b.Informative), b.Id)).ToList());
        SetField(p, "_contributions", n.Contributions.Where(c => c.MemberId != Period.CarryoverSource).Select(c => Build(new Contribution(c.MemberId, M(c.Paid), c.CategoryId, c.FundId, c.Date), c.Id)).ToList());
        SetField(p, "_budgets", n.Budgets.Select(b => Build(new Budget(b.CategoryId, M(b.Allocated), b.AlertThreshold, b.NotifyOnEveryExpense), b.Id)).ToList());
        SetField(p, "_expenses", n.Expenses.Select(e => Build(new Expense(e.CategoryId, M(e.Amount), e.Date, e.MemberId, e.FundId, e.Note, e.SourceSavingCategoryId), e.Id)).ToList());
        SetField(p, "_savingAllocations", n.SavingAllocations.Select(a => Build(new SavingAllocation(a.SavingCategoryId, M(a.Amount), a.Date, a.Note, a.SourceExpenseId, a.BudgetCategoryId, a.TransferPairId), a.Id)).ToList());
        SetField(p, "_fundTransfers", n.FundTransfers.Select(t => Build(new FundTransfer(t.FromFundId, t.ToFundId, M(t.Amount), t.Date, t.Note), t.Id)).ToList());
        SetField(p, "_externalTransfers", (n.ExternalTransfers ?? []).Select(t => Build(new ExternalTransfer(t.FundId, M(t.Amount), t.Date, t.ToAccountId, t.Note), t.Id)).ToList());
        return p;
    }

    // --- reflection helpers ----------------------------------------------

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static T Build<T>(T entity, Guid id) where T : Entity
    {
        SetId(entity, id);
        return entity;
    }

    private static void SetId(Entity entity, Guid id) => SetAuto(entity, nameof(Entity.Id), id);

    private static void SetAuto(object target, string propertyName, object? value) =>
        FindField(target.GetType(), $"<{propertyName}>k__BackingField").SetValue(target, value);

    private static void SetField(object target, string fieldName, object? value) =>
        FindField(target.GetType(), fieldName).SetValue(target, value);

    private static FieldInfo FindField(Type? type, string name)
    {
        for (; type is not null; type = type.BaseType)
            if (type.GetField(name, Flags) is { } field)
                return field;
        throw new InvalidOperationException($"Field '{name}' was not found.");
    }

    // --- JSON node shapes (flat, decimals carry the account currency) -----

    private record AccountNode(Guid Id, string Name, string Currency, Guid OwnerUserId,
        List<MemberNode> Members, List<FundNode> Funds, List<CategoryNode> Categories,
        List<SavingCategoryNode> SavingCategories, List<PeriodNode> Periods,
        List<ContributionCategoryNode>? ContributionCategories = null);

    private record MemberNode(Guid Id, Guid UserId, string DisplayName);
    private record ContributionCategoryNode(Guid Id, string Name);
    private record FundNode(Guid Id, string Name, Guid? ParentId, string? Note = null);
    private record CategoryNode(Guid Id, string Name, Guid? ParentId);
    private record SavingCategoryNode(Guid Id, string Name, Guid? ParentId, decimal? GoalAmount, decimal AlertThreshold, bool NotifyOnMilestone, decimal InitialAmount);

    private record PeriodNode(Guid Id, string Currency, DateOnly From, DateOnly To, PeriodStatus Status, decimal CarriedIn,
        List<InitialBalanceNode> InitialBalances, List<ContributionNode> Contributions, List<BudgetNode> Budgets,
        List<ExpenseNode> Expenses, List<SavingAllocationNode> SavingAllocations, List<FundTransferNode> FundTransfers,
        List<ExternalTransferNode>? ExternalTransfers = null);

    private record InitialBalanceNode(Guid Id, Guid FundId, decimal Amount, bool Informative);
    private record ContributionNode(Guid Id, Guid MemberId, decimal Paid,
        Guid CategoryId = default, Guid FundId = default, DateOnly Date = default);
    private record BudgetNode(Guid Id, Guid CategoryId, decimal Allocated, decimal AlertThreshold, bool NotifyOnEveryExpense);
    private record ExpenseNode(Guid Id, Guid CategoryId, decimal Amount, DateOnly Date, Guid MemberId, Guid FundId, string? Note, Guid? SourceSavingCategoryId);
    private record SavingAllocationNode(Guid Id, Guid SavingCategoryId, decimal Amount, DateOnly Date, string? Note, Guid? SourceExpenseId, Guid? BudgetCategoryId = null, Guid? TransferPairId = null);
    private record FundTransferNode(Guid Id, Guid FromFundId, Guid ToFundId, decimal Amount, DateOnly Date, string? Note);
    private record ExternalTransferNode(Guid Id, Guid FundId, decimal Amount, DateOnly Date, Guid? ToAccountId, string? Note);
}
