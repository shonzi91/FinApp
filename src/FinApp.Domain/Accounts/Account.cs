using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;

namespace FinApp.Domain.Accounts;

/// <summary>
/// A first-level account (Personal, Shared, Family...). The aggregate root: it owns members,
/// the shared category and savings-category trees, and the ordered list of periods.
/// </summary>
public sealed class Account : Entity
{
    private readonly List<AccountMember> _members = [];
    private readonly List<Category> _categories = [];
    private readonly List<SavingCategory> _savingCategories = [];
    private readonly List<ContributionCategory> _contributionCategories = [];
    private readonly List<Fund> _funds = [];
    private readonly List<Period> _periods = [];

    public string Name { get; private set; }
    public string Currency { get; }

    /// <summary>
    /// The user who created this account. Owner-only actions (rename, delete) check this; everything
    /// inside the account may be changed by any contributor. <see cref="Guid.Empty"/> for accounts
    /// created without a signed-in user (e.g. unit tests).
    /// </summary>
    public Guid OwnerUserId { get; private set; }

    public IReadOnlyList<AccountMember> Members => _members;

    /// <summary>All categories, flat. Use <see cref="RootCategories"/> / <see cref="ChildrenOfCategory"/> for the tree.</summary>
    public IReadOnlyList<Category> Categories => _categories;

    /// <summary>All savings buckets, flat.</summary>
    public IReadOnlyList<SavingCategory> SavingCategories => _savingCategories;

    /// <summary>Account-level contribution categories (Salary, Vouchers…), referenced by id from deposits.</summary>
    public IReadOnlyList<ContributionCategory> ContributionCategories => _contributionCategories;

    /// <summary>All funds (places money lives), flat. Referenced by id from expenses, opening balances and transfers.</summary>
    public IReadOnlyList<Fund> Funds => _funds;

    public IEnumerable<Category> RootCategories => _categories.Where(c => c.IsRoot);
    public IEnumerable<Category> ChildrenOfCategory(Guid parentId) => _categories.Where(c => c.ParentId == parentId);
    public IEnumerable<SavingCategory> RootSavingCategories => _savingCategories.Where(c => c.IsRoot);

    /// <summary>Periods ordered oldest → newest.</summary>
    public IReadOnlyList<Period> Periods => _periods;

    public Account(string name, string currency)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Account name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        Name = name.Trim();
        Currency = currency.ToUpperInvariant();
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Account name is required.", nameof(name));
        Name = name.Trim();
    }

    // --- Membership & sharing --------------------------------------------

    public AccountMember AddMember(Guid userId, string displayName)
    {
        if (_members.Any(m => m.UserId == userId))
            throw new InvalidOperationException("User is already a member of this account.");
        var member = new AccountMember(userId, displayName);
        _members.Add(member);
        return member;
    }

    /// <summary>
    /// Record the creating user as owner and add them as the first contributor (member). Call once,
    /// at account creation, before there are any other members.
    /// </summary>
    public void AssignOwner(Guid userId, string displayName)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("Owner user id is required.", nameof(userId));
        if (OwnerUserId != Guid.Empty)
            throw new InvalidOperationException("This account already has an owner.");
        OwnerUserId = userId;
        AddMember(userId, displayName);
    }

    /// <summary>Add an invited user as a contributor. Contributors are unified with members.</summary>
    public AccountMember AddContributor(Guid userId, string displayName) => AddMember(userId, displayName);

    /// <summary>True for the account creator — gates rename/delete of the account itself.</summary>
    public bool IsOwner(Guid userId) => userId != Guid.Empty && OwnerUserId == userId;

    /// <summary>True for any user who can edit inside the account (owner or invited contributor).</summary>
    public bool IsContributor(Guid userId) => _members.Any(m => m.UserId == userId);

    // --- Categories -------------------------------------------------------

    /// <summary>Add a category. Pass <paramref name="parentId"/> to make it a sub-category (e.g. Kids → Kid1).</summary>
    public Category AddCategory(string name, Guid? parentId = null)
    {
        if (parentId is { } pid && _categories.All(c => c.Id != pid))
            throw new InvalidOperationException("Parent category does not exist in this account.");
        if (_categories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A category named “{name.Trim()}” already exists.");
        var category = new Category(name, parentId);
        _categories.Add(category);
        return category;
    }

    /// <summary>Add a savings bucket. Pass <paramref name="parentId"/> to make it a sub-bucket.</summary>
    public SavingCategory AddSavingCategory(string name, Guid? parentId = null)
    {
        if (parentId is { } pid && _savingCategories.All(c => c.Id != pid))
            throw new InvalidOperationException("Parent saving category does not exist in this account.");
        if (_savingCategories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A savings bucket named “{name.Trim()}” already exists.");
        var category = new SavingCategory(name, parentId);
        _savingCategories.Add(category);
        return category;
    }

    /// <summary>Case-insensitive, trimmed name comparison used to reject duplicate names within the account.</summary>
    private static bool NameEquals(string existing, string candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    public Category? FindCategory(Guid categoryId) => _categories.FirstOrDefault(c => c.Id == categoryId);
    public SavingCategory? FindSavingCategory(Guid id) => _savingCategories.FirstOrDefault(c => c.Id == id);

    public void RenameCategory(Guid categoryId, string name)
    {
        var category = FindCategory(categoryId) ?? throw new InvalidOperationException("Category not found.");
        if (_categories.Any(c => c.Id != categoryId && NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A category named “{name.Trim()}” already exists.");
        category.Rename(name);
    }

    /// <summary>Why a category can't be removed, or null when it can.</summary>
    public string? CategoryRemovalBlocker(Guid categoryId)
    {
        if (_categories.Any(c => c.ParentId == categoryId))
            return "it has sub-categories";
        if (_periods.SelectMany(p => p.Budgets).Any(b => b.CategoryId == categoryId))
            return "a budget references it";
        if (_periods.SelectMany(p => p.Expenses).Any(e => e.CategoryId == categoryId))
            return "expenses reference it";
        return null;
    }

    public void RemoveCategory(Guid categoryId)
    {
        var blocker = CategoryRemovalBlocker(categoryId);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove category: {blocker}.");
        var category = FindCategory(categoryId)
            ?? throw new InvalidOperationException("Category not found.");
        _categories.Remove(category);
    }

    public void RenameSavingCategory(Guid savingCategoryId, string name)
    {
        var bucket = FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found.");
        if (_savingCategories.Any(c => c.Id != savingCategoryId && NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A savings bucket named “{name.Trim()}” already exists.");
        bucket.Rename(name);
    }

    /// <summary>Set or clear a savings bucket's goal and alert settings.</summary>
    public void ConfigureSavingGoal(Guid savingCategoryId, decimal? goalAmount, decimal alertThreshold = 0.80m, bool notifyOnMilestone = false) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .SetGoal(goalAmount, alertThreshold, notifyOnMilestone);

    /// <summary>Set a savings bucket's pre-existing initial balance (setup-time only; see <see cref="SavingCategory.InitialAmount"/>).</summary>
    public void SetSavingInitialAmount(Guid savingCategoryId, decimal amount) =>
        (FindSavingCategory(savingCategoryId) ?? throw new InvalidOperationException("Saving category not found."))
            .SetInitialAmount(amount);

    /// <summary>Why a savings bucket can't be removed, or null when it can.</summary>
    public string? SavingCategoryRemovalBlocker(Guid savingCategoryId)
    {
        if (_savingCategories.Any(c => c.ParentId == savingCategoryId))
            return "it has sub-buckets";
        if (_periods.SelectMany(p => p.SavingAllocations).Any(a => a.SavingCategoryId == savingCategoryId))
            return "it has savings activity";
        return null;
    }

    public void RemoveSavingCategory(Guid savingCategoryId)
    {
        var blocker = SavingCategoryRemovalBlocker(savingCategoryId);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove saving bucket: {blocker}.");
        var category = FindSavingCategory(savingCategoryId)
            ?? throw new InvalidOperationException("Saving category not found.");
        _savingCategories.Remove(category);
    }

    // --- Contribution categories -----------------------------------------

    public ContributionCategory AddContributionCategory(string name)
    {
        if (_contributionCategories.Any(c => NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A contribution category named “{name.Trim()}” already exists.");
        var category = new ContributionCategory(name);
        _contributionCategories.Add(category);
        return category;
    }

    public ContributionCategory? FindContributionCategory(Guid id) => _contributionCategories.FirstOrDefault(c => c.Id == id);

    public void RenameContributionCategory(Guid id, string name)
    {
        var category = FindContributionCategory(id) ?? throw new InvalidOperationException("Contribution category not found.");
        if (_contributionCategories.Any(c => c.Id != id && NameEquals(c.Name, name)))
            throw new InvalidOperationException($"A contribution category named “{name.Trim()}” already exists.");
        category.Rename(name);
    }

    /// <summary>Why a contribution category can't be removed, or null when it can.</summary>
    public string? ContributionCategoryRemovalBlocker(Guid id)
    {
        if (_periods.SelectMany(p => p.Contributions).Any(c => c.CategoryId == id))
            return "deposits reference it";
        return null;
    }

    public void RemoveContributionCategory(Guid id)
    {
        var blocker = ContributionCategoryRemovalBlocker(id);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove contribution category: {blocker}.");
        var category = FindContributionCategory(id) ?? throw new InvalidOperationException("Contribution category not found.");
        _contributionCategories.Remove(category);
    }

    // --- Funds ------------------------------------------------------------

    /// <summary>Add a fund. Pass <paramref name="parentId"/> to nest it as an informational sub-fund.</summary>
    public Fund AddFund(string name, Guid? parentId = null)
    {
        if (parentId is { } pid)
        {
            var parent = FindFund(pid)
                ?? throw new InvalidOperationException("Parent fund does not exist in this account.");
            if (!parent.IsRoot)
                throw new InvalidOperationException("Sub-funds can only be nested one level deep.");
        }
        if (_funds.Any(f => NameEquals(f.Name, name)))
            throw new InvalidOperationException($"A fund named “{name.Trim()}” already exists.");
        var fund = new Fund(name, parentId);
        _funds.Add(fund);
        return fund;
    }

    public IEnumerable<Fund> RootFunds => _funds.Where(f => f.IsRoot);
    public IEnumerable<Fund> ChildFundsOf(Guid parentId) => _funds.Where(f => f.ParentId == parentId);

    /// <summary>Add the standard starter funds to a new account.</summary>
    public void AddDefaultFunds()
    {
        foreach (var name in new[] { "Bank", "Cash", "Digital wallet", "Other" })
            AddFund(name);
    }

    public Fund? FindFund(Guid fundId) => _funds.FirstOrDefault(f => f.Id == fundId);
    public string FundName(Guid fundId) => FindFund(fundId)?.Name ?? "—";

    /// <summary>Id of the seeded fund with the given name (case-insensitive); throws if none. Handy for tests/defaults.</summary>
    public Guid FundId(string name) =>
        (_funds.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No fund named '{name}'.")).Id;

    public void RenameFund(Guid fundId, string name)
    {
        var fund = FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.");
        if (_funds.Any(f => f.Id != fundId && NameEquals(f.Name, name)))
            throw new InvalidOperationException($"A fund named “{name.Trim()}” already exists.");
        fund.Rename(name);
    }

    public void SetFundNote(Guid fundId, string? note)
    {
        var fund = FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.");
        fund.SetNote(note);
    }

    /// <summary>
    /// Why a fund can't be removed, or null when it can. Opening balances are <b>not</b> a hard blocker —
    /// they can be moved to another fund on removal (see <see cref="RemoveFund"/> / <see cref="FundHasOpeningBalance"/>).
    /// </summary>
    public string? FundRemovalBlocker(Guid fundId)
    {
        if (_funds.Any(f => f.ParentId == fundId))
            return "it has sub-funds";
        if (FindFund(fundId)?.IsRoot == true && _funds.Count(f => f.IsRoot) <= 1)
            return "it's the only fund";
        if (_periods.SelectMany(p => p.Expenses).Any(e => e.FundId == fundId))
            return "expenses reference it";
        if (_periods.SelectMany(p => p.FundTransfers).Any(t => t.FromFundId == fundId || t.ToFundId == fundId))
            return "a transfer references it";
        return null;
    }

    /// <summary>True when the fund has a non-zero real opening balance in any period (which must be moved before removal). Zero balances and sub-fund informative balances don't count.</summary>
    public bool FundHasOpeningBalance(Guid fundId) =>
        _periods.SelectMany(p => p.InitialBalances).Any(b => b.FundId == fundId && !b.Informative && !b.Amount.IsZero);

    /// <summary>
    /// Remove a fund. Optionally pass <paramref name="moveOpeningBalancesTo"/> to consolidate its opening
    /// balances onto another (top-level) fund first — total-preserving. When no target is given the balance
    /// is simply dropped along with the fund.
    /// </summary>
    public void RemoveFund(Guid fundId, Guid? moveOpeningBalancesTo = null)
    {
        var blocker = FundRemovalBlocker(fundId);
        if (blocker is not null)
            throw new InvalidOperationException($"Cannot remove fund: {blocker}.");
        var fund = FindFund(fundId) ?? throw new InvalidOperationException("Fund not found.");

        if (moveOpeningBalancesTo is { } targetId)
        {
            if (targetId == fundId)
                throw new InvalidOperationException("Choose a different fund to receive the opening balance.");
            var target = FindFund(targetId) ?? throw new InvalidOperationException("Target fund not found.");
            if (!target.IsRoot)
                throw new InvalidOperationException("Opening balances can only move to a top-level fund.");
            foreach (var period in _periods)
                period.MoveInitialBalance(fundId, targetId);
        }

        // Drop any remaining opening-balance rows (the moved-from rows are already gone; this also discards
        // the balance when the user chose not to transfer, and clears any informative sub-fund rows).
        foreach (var period in _periods)
            period.RemoveInitialBalance(fundId);

        _funds.Remove(fund);
    }

    /// <summary>A category id plus all descendant ids — used to roll expenses up to a parent budget.</summary>
    public IReadOnlyCollection<Guid> CategoryWithDescendantIds(Guid categoryId) =>
        WithDescendants(categoryId, _categories.Select(c => (c.Id, c.ParentId)));

    /// <summary>A savings bucket id plus all descendant ids.</summary>
    public IReadOnlyCollection<Guid> SavingCategoryWithDescendantIds(Guid savingCategoryId) =>
        WithDescendants(savingCategoryId, _savingCategories.Select(c => (c.Id, c.ParentId)));

    private static IReadOnlyCollection<Guid> WithDescendants(Guid rootId, IEnumerable<(Guid Id, Guid? ParentId)> nodes)
    {
        var byParent = nodes.ToLookup(n => n.ParentId);
        var result = new List<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            result.Add(id);
            foreach (var child in byParent[id])
                queue.Enqueue(child.Id);
        }
        return result;
    }

    // --- Periods ----------------------------------------------------------

    public Period? CurrentPeriod => _periods.LastOrDefault();

    /// <summary>
    /// Remove the latest period and re-activate the previous one (which becomes editable again).
    /// Only the most recent period can be removed, so the period chain stays contiguous.
    /// </summary>
    public void RemoveLatestPeriod()
    {
        if (_periods.Count <= 1)
            throw new InvalidOperationException("Cannot remove the only period.");
        _periods.RemoveAt(_periods.Count - 1);
        _periods[^1].Reopen();
    }

    public Period? PreviousPeriodOf(Period period)
    {
        var index = _periods.IndexOf(period);
        return index > 0 ? _periods[index - 1] : null;
    }

    public int IndexOfPeriod(Period period) => _periods.IndexOf(period);

    /// <summary>
    /// Reschedule a period's date range and shift every later period to stay contiguous,
    /// preserving each one's length (feature: "set from/to, all periods shift").
    /// </summary>
    public void ReschedulePeriod(Period period, DateOnly from, DateOnly to)
    {
        period.Reschedule(from, to);

        for (var i = _periods.IndexOf(period) + 1; i > 0 && i < _periods.Count; i++)
        {
            var newFrom = _periods[i - 1].To.AddDays(1);
            var newTo = newFrom.AddDays(_periods[i].LengthInDays);
            _periods[i].Reschedule(newFrom, newTo);
        }
    }

    /// <summary>
    /// Start a new period. Optionally copies the previous period's budget allocations and alert
    /// settings forward (feature 5). Carry-over of opening balances and reconciliation are handled
    /// by the application/reconciliation services, not here, to keep this aggregate pure.
    /// </summary>
    public Period StartPeriod(DateOnly from, DateOnly to, bool copyBudgetsFromPrevious = false)
    {
        var previous = CurrentPeriod;
        if (previous is not null && from <= previous.From)
            throw new InvalidOperationException("A new period must start after the current period.");

        var period = new Period(Currency, from, to);

        if (copyBudgetsFromPrevious && previous is not null)
        {
            foreach (var b in previous.Budgets)
                period.AddBudget(b.CategoryId, b.Allocated, b.AlertThreshold, b.NotifyOnEveryExpense);
        }

        _periods.Add(period);
        return period;
    }
}
