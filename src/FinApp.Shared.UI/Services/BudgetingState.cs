using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Application state the Blazor UI binds to, now backed by the sync server. Holds the signed-in user's
/// account summaries, the loaded full aggregate for the selected account, and the period being viewed.
/// The UI mutates the loaded aggregate through domain methods; every mutation re-serializes the account
/// and pushes the snapshot to the server (which relays the change to other contributors).
/// </summary>
public sealed class BudgetingState(FinAppApiClient api, AuthState auth, SyncClient sync)
{
    private readonly BudgetCoverageService _coverage = new();
    private readonly SavingsReportService _savings = new();

    private List<AccountSummaryDto> _summaries = [];
    private Account? _account;
    private long _version;
    private int _accountIndex;
    private int _selectedIndex;
    private bool _syncStarted;
    private List<InvitationDto> _pendingInvitations = [];

    // Per-account aggregate cache so switching back to an already-loaded account is instant (no re-fetch).
    // It's only trusted while live sync is connected: AccountChanged drops a changed account's entry, and a
    // reconnect clears everything (events during the outage are missed). Falls back to always-fetch when offline.
    private sealed class CachedAccount(Account account, long version)
    {
        public Account Account { get; } = account;
        public long Version { get; set; } = version;
    }
    private readonly Dictionary<Guid, CachedAccount> _cache = [];

    public bool IsReady { get; private set; }
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (IsReady || !auth.IsAuthenticated) return;

        if (!_syncStarted)
        {
            sync.AccountChanged += OnAccountChanged;
            sync.InvitationReceived += OnInvitationReceived;
            sync.Reconnected += OnReconnected;
            try { await sync.StartAsync(); } catch { /* live sync is best-effort; REST still works */ }
            _syncStarted = true;
        }

        _summaries = await api.GetAccountsAsync();
        _accountIndex = 0;
        await SubscribeAllAsync();   // so AccountChanged invalidates cached background accounts too
        await LoadSelectedAccountAsync();
        await RefreshInvitationsAsync();

        IsReady = true;
        Changed?.Invoke();
    }

    /// <summary>Clear all session state on sign-out.</summary>
    public async Task ResetAsync()
    {
        IsReady = false;
        _summaries = [];
        _account = null;
        _version = 0;
        _accountIndex = 0;
        _selectedIndex = 0;
        _pendingInvitations = [];
        _syncStarted = false;
        _cache.Clear();
        sync.AccountChanged -= OnAccountChanged;
        sync.InvitationReceived -= OnInvitationReceived;
        sync.Reconnected -= OnReconnected;
        await sync.StopAsync();
        Changed?.Invoke();
    }

    // --- Accounts ---------------------------------------------------------

    public bool HasAccounts => _summaries.Count > 0;
    public Account Account => _account!;
    public IReadOnlyList<AccountSummaryDto> Accounts => _summaries;
    public Guid CurrentAccountId => _account?.Id ?? Guid.Empty;

    /// <summary>True when the signed-in user owns the current account (gates rename/delete).</summary>
    public bool IsOwnerOfCurrent => _account is not null && _account.IsOwner(auth.UserId);

    public async Task SwitchAccount(Guid accountId)
    {
        var index = _summaries.FindIndex(a => a.Id == accountId);
        if (index < 0 || index == _accountIndex) return;
        _accountIndex = index;
        await LoadSelectedAccountAsync();
        Changed?.Invoke();
    }

    public async Task AddAccount(string name, string currency, decimal savingsRateTarget = 0.20m)
    {
        if (_summaries.Any(a => NameEquals(a.Name, name)))
            throw new InvalidOperationException($"You already have an account named “{name.Trim()}”.");
        var summary = await api.CreateAccountAsync(new CreateAccountRequest(name, currency));
        _summaries.Add(summary);
        _accountIndex = _summaries.Count - 1;
        await LoadSelectedAccountAsync(); // empty snapshot -> seeds the starter body and saves
        if (savingsRateTarget != _account!.SavingsRateTarget)
        {
            _account.SetSavingsRateTarget(savingsRateTarget);
            await PushSnapshotAsync();
        }
        Changed?.Invoke();
    }

    /// <summary>The account's target savings rate (fraction 0..1) — drives the Insights gauge/score.</summary>
    public decimal SavingsRateTarget => Account.SavingsRateTarget;

    /// <summary>Set the account's target savings rate (fraction 0..1) and push the snapshot.</summary>
    public Task SetSavingsRateTarget(decimal target)
    {
        Account.SetSavingsRateTarget(target);
        return SaveAsync();
    }

    public async Task RenameAccount(string name)
    {
        var id = CurrentAccountId;
        if (_summaries.Any(a => a.Id != id && NameEquals(a.Name, name)))
            throw new InvalidOperationException($"You already have an account named “{name.Trim()}”.");
        await api.RenameAccountAsync(id, name);
        _account!.Rename(name);
        _summaries[_accountIndex] = _summaries[_accountIndex] with { Name = name };
        Changed?.Invoke();
    }

    public async Task RemoveAccount(Guid accountId)
    {
        await api.DeleteAccountAsync(accountId);
        _cache.Remove(accountId);
        var index = _summaries.FindIndex(a => a.Id == accountId);
        if (index >= 0) _summaries.RemoveAt(index);
        if (_accountIndex >= _summaries.Count)
            _accountIndex = Math.Max(0, _summaries.Count - 1);
        await LoadSelectedAccountAsync();
        Changed?.Invoke();
    }

    // --- Membership / archiving -------------------------------------------

    /// <summary>The current account's other members (everyone but the signed-in user).</summary>
    public IReadOnlyList<MemberDto> OtherMembers => RealUsers.Where(m => m.UserId != auth.UserId).ToList();

    public Guid MyUserId => auth.UserId;

    /// <summary>The current account's owner (from the server-authoritative summary).</summary>
    public Guid CurrentOwnerId => _summaries.ElementAtOrDefault(_accountIndex)?.OwnerUserId ?? Guid.Empty;

    /// <summary>Leave the current account. Returns whether it was archived (you were the last member) or just left.</summary>
    public async Task<LeaveAccountResult> LeaveCurrentAccount(Guid? newOwnerUserId)
    {
        var id = CurrentAccountId;
        var result = await api.LeaveAccountAsync(id, newOwnerUserId);
        _cache.Remove(id);
        var index = _summaries.FindIndex(a => a.Id == id);
        if (index >= 0) _summaries.RemoveAt(index);   // dropped from the active list either way
        if (_accountIndex >= _summaries.Count)
            _accountIndex = Math.Max(0, _summaries.Count - 1);
        await LoadSelectedAccountAsync();
        Changed?.Invoke();
        return result;
    }

    /// <summary>Owner removes another member from the current account.</summary>
    public async Task RemoveMember(Guid memberUserId)
    {
        var id = CurrentAccountId;
        await api.RemoveMemberAsync(id, memberUserId);
        await ReloadSummariesKeepingAsync(id);
        await LoadSelectedAccountAsync(forceRefresh: true);
        Changed?.Invoke();
    }

    /// <summary>Owner hands ownership of the current account to another member.</summary>
    public async Task TransferOwnership(Guid newOwnerUserId)
    {
        var id = CurrentAccountId;
        await api.TransferOwnershipAsync(id, newOwnerUserId);
        await ReloadSummariesKeepingAsync(id);
        await LoadSelectedAccountAsync(forceRefresh: true);
        Changed?.Invoke();
    }

    public Task<List<ArchivedAccountDto>> GetArchivedAccounts() => api.GetArchivedAccountsAsync();

    public async Task ReactivateAccount(Guid accountId)
    {
        await api.ReactivateAccountAsync(accountId);
        await ReloadSummariesKeepingAsync(accountId);
        await LoadSelectedAccountAsync(forceRefresh: true);
        Changed?.Invoke();
    }

    private async Task ReloadSummariesKeepingAsync(Guid accountId)
    {
        _summaries = await api.GetAccountsAsync();
        var idx = _summaries.FindIndex(a => a.Id == accountId);
        _accountIndex = idx >= 0 ? idx : Math.Max(0, Math.Min(_accountIndex, _summaries.Count - 1));
    }

    private async Task LoadSelectedAccountAsync(bool forceRefresh = false)
    {
        if (_summaries.Count == 0) { _account = null; _version = 0; return; }

        var summary = _summaries[_accountIndex];

        // Load member profile pictures for this account (fire-and-forget so account switching stays instant).
        if (_avatarsAccountId != summary.Id)
        {
            _avatarsAccountId = summary.Id;
            _ = RefreshMemberAvatarsAsync(summary.Id);
        }

        // Warm-cache hit: render the already-loaded aggregate instantly, no server round-trip. Only trusted
        // while live sync is connected (otherwise we can't know if a contributor changed it behind our back).
        if (!forceRefresh && sync.IsConnected && _cache.TryGetValue(summary.Id, out var hit))
        {
            _account = hit.Account;
            _version = hit.Version;
            ReconcileHeader(_account, summary);
            _selectedIndex = _account.Periods.Count - 1;
            return;
        }

        var snapshot = await api.GetSnapshotAsync(summary.Id);
        _version = snapshot.Version;

        if (string.IsNullOrEmpty(snapshot.Payload))
        {
            // Brand-new account: build from the header, seed the starter body, and save v1.
            _account = AccountSnapshotSerializer.CreateForHeader(
                summary.Id, summary.Name, summary.Currency, summary.OwnerUserId,
                summary.Members.Select(m => (m.UserId, m.DisplayName)));
            SeedStarterBody(_account);
            await PushSnapshotAsync();
        }
        else
        {
            _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
            ReconcileHeader(_account, summary);
        }

        _cache[summary.Id] = new CachedAccount(_account, _version);
        _selectedIndex = _account.Periods.Count - 1;
        await sync.SubscribeAsync(summary.Id);
    }

    /// <summary>Ensure the loaded aggregate reflects server-authoritative header data (name + members).</summary>
    private static void ReconcileHeader(Account account, AccountSummaryDto summary)
    {
        if (account.Name != summary.Name) account.Rename(summary.Name);
        foreach (var m in summary.Members)
            if (!account.IsContributor(m.UserId))
                account.AddMember(m.UserId, m.DisplayName);
    }

    /// <summary>Serialize the current aggregate and push it to the server, advancing the version.</summary>
    private async Task PushSnapshotAsync()
    {
        var payload = AccountSnapshotSerializer.Serialize(_account!);
        var saved = await api.SaveSnapshotAsync(_account!.Id, new SaveAccountRequest(payload, _version));
        _version = saved.Version;
        // Keep the cache entry's version in step with our own push (the Account is the same live instance).
        if (_cache.TryGetValue(_account.Id, out var c)) c.Version = _version;
        else _cache[_account.Id] = new CachedAccount(_account, _version);
    }

    // --- Period navigation ------------------------------------------------

    public Period Period => Account.Periods[_selectedIndex];
    public int PeriodNumber => _selectedIndex + 1;
    public int PeriodCount => Account.Periods.Count;
    public bool CanGoPrev => _selectedIndex > 0;
    public bool CanGoNext => _selectedIndex < Account.Periods.Count - 1;
    public bool IsLatestPeriod => _selectedIndex == Account.Periods.Count - 1;

    public void GoPrev() { if (CanGoPrev) { _selectedIndex--; Changed?.Invoke(); } }
    public void GoNext() { if (CanGoNext) { _selectedIndex++; Changed?.Invoke(); } }

    public string Currency => Account.Currency;
    public Money Money(decimal amount) => new(amount, Currency);

    // --- Funds ------------------------------------------------------------

    public IReadOnlyList<Fund> Funds => Account.Funds;
    /// <summary>All funds (flat). Kept as <c>RootFunds</c> for call-site compatibility.</summary>
    public IReadOnlyList<Fund> RootFunds => Account.RootFunds.ToList();
    public Fund? FindFund(Guid fundId) => Account.FindFund(fundId);
    public string? FundNote(Guid fundId) => Account.FindFund(fundId)?.Note;
    public string FundName(Guid fundId) => Account.FundName(fundId);
    public Money FundBalance(Guid fundId) => Period.FundBalance(fundId);
    public Money FundOpeningBalance(Guid fundId) =>
        Period.InitialBalances.FirstOrDefault(b => b.FundId == fundId)?.Amount ?? Money(0);
    public string? FundRemovalBlocker(Guid fundId) => Account.FundRemovalBlocker(fundId);

    public IReadOnlyList<FundTransfer> FundTransfers =>
        Period.FundTransfers.OrderByDescending(t => t.Date).ToList();

    private Guid DefaultFundId => SelectableFunds.FirstOrDefault()?.Id ?? DefaultFundIdRaw;
    private Guid DefaultFundIdRaw => Account.RootFunds.FirstOrDefault()?.Id ?? Guid.Empty;

    /// <summary>The period's opening balance: the sum of the real (non-informative) initial fund values.
    /// Independent of how the money is later budgeted/saved (unallocations never change it).</summary>
    public Money OpeningBalance => Period.InitialTotal;

    /// <summary>Physical money expected to carry into the next period.</summary>
    public Money ClosingBalance => Period.ExpectedClosingBalance;

    /// <summary>This period's transfers sent out to other accounts (newest first).</summary>
    public IReadOnlyList<ExternalTransfer> ExternalTransfers =>
        Period.ExternalTransfers.OrderByDescending(t => t.Date).ToList();

    // --- Category tree & budgets (reads) ----------------------------------

    public IEnumerable<Category> RootCategories => Account.RootCategories;
    public IEnumerable<Category> ChildrenOf(Guid parentId) => Account.ChildrenOfCategory(parentId);
    public IReadOnlyList<Category> AllCategories => Account.Categories;

    /// <summary>Categories in tree order with their depth, for an indented &lt;select&gt; (parents above their children).</summary>
    public IReadOnlyList<(Category Category, int Depth)> CategoryOptions
    {
        get
        {
            var result = new List<(Category, int)>();
            void Walk(IEnumerable<Category> nodes, int depth)
            {
                foreach (var c in nodes)
                {
                    result.Add((c, depth));
                    Walk(Account.ChildrenOfCategory(c.Id), depth + 1);
                }
            }
            Walk(Account.RootCategories, 0);
            return result;
        }
    }
    public Budget? BudgetFor(Guid categoryId) => Period.FindBudget(categoryId);
    public bool HasBudget(Guid categoryId) => Period.FindBudget(categoryId) is not null;
    public BudgetCoverage Coverage(Guid categoryId) => _coverage.ForCategory(Account, Period, categoryId);
    public Money Leftover(Guid categoryId) => Coverage(categoryId).Remaining;
    public string? CategoryRemovalBlocker(Guid categoryId) => Account.CategoryRemovalBlocker(categoryId);
    public string CategoryName(Guid categoryId) => Account.FindCategory(categoryId)?.Name ?? "—";
    public string? ParentName(Guid? parentId) => parentId is { } p ? Account.FindCategory(p)?.Name : null;

    public IEnumerable<Category> BudgetedCategories =>
        Period.Budgets.Select(b => Account.FindCategory(b.CategoryId)!).Where(c => c is not null);

    /// <summary>Total spent in a category and its sub-categories this period (works without a budget).</summary>
    public Money SpentInCategory(Guid categoryId)
    {
        var ids = Account.CategoryWithDescendantIds(categoryId).ToHashSet();
        return Period.Expenses.Where(e => ids.Contains(e.CategoryId))
            .Select(e => e.Amount)
            .Aggregate(Money(0), (acc, m) => acc + m);
    }

    // --- Totals & reports -------------------------------------------------

    public Money TotalBudgeted => Period.BudgetedTotal;
    public Money TotalSpent => Period.ExpensesTotal;

    /// <summary>New member deposits this period (the contributed pool).</summary>
    public Money TotalContributed => Period.ContributionsPaidTotal;

    /// <summary>Savings earmarked beyond actual cash left — overspend to reconcile next period.</summary>
    public Money Deficit => Period.Deficit;
    public bool HasDeficit => Period.Deficit.Amount > 0m;

    /// <summary>
    /// Savings accumulated <b>before</b> this period — total saved across the whole account (incl. pre-app initial
    /// balances) minus this period's own net. The opening balances carry that money forward, so the planning caps
    /// must reserve it: otherwise previously-saved money looks freshly available to budget, save or transfer again.
    /// </summary>
    private Money PriorSaved => _savings.AccumulatedTotal(Account) - Period.SavingsNetTotal;

    /// <summary>Most that can be sent to another account without breaking the savings earmark.</summary>
    public Money AvailableToTransferOut => Period.AvailableToTransferOutAfter(PriorSaved);

    /// <summary>Most that can be sent to another account from a specific fund (≤ that fund's balance).</summary>
    public Money AvailableToTransferOutFromFund(Guid fundId) => Period.AvailableToTransferOutFromFundAfter(fundId, PriorSaved);
    public Money SavingsThisPeriod => Period.SavingsNetTotal;
    public Money SavingsAccumulated => _savings.AccumulatedTotal(Account);
    public Money MaxAdditionalSavings => Period.MaxAdditionalSavingsAfter(PriorSaved);
    public Money AvailableToSave => Period.AvailableToSaveAfter(PriorSaved);

    /// <summary>Unallocated cash this period (closing − all savings). Negative = over-allocated. Advisory only.</summary>
    public Money FreeToAllocate => Period.FreeToAllocateAfter(PriorSaved);
    public bool IsOverAllocated => Period.FreeToAllocateAfter(PriorSaved).IsNegative;

    /// <summary>The most a single category's budget can be set to (Current − savings + spent, minus other budgets). Caps budgeting.</summary>
    public Money MaxBudgetFor(Guid categoryId) => Period.MaxBudgetFor(categoryId, PriorSaved);

    public IReadOnlyList<Expense> AllExpenses =>
        Period.Expenses.OrderByDescending(e => e.Date).ToList();

    public IReadOnlyList<Expense> ExpensesFor(Guid categoryId) =>
        Period.Expenses.Where(e => e.CategoryId == categoryId).OrderByDescending(e => e.Date).ToList();

    public bool IsPeriodOpen => Period.Status == PeriodStatus.Open;

    public Expense? FindExpense(Guid id) => Period.Expenses.FirstOrDefault(e => e.Id == id);

    public decimal? PeriodSavingsRate => _savings.PeriodSavingsRate(Period);
    public decimal? AccountSavingsRate => _savings.AccountSavingsRate(Account);

    public SavingCategory? FindSavingBucket(Guid id) => Account.FindSavingCategory(id);
    public SavingGoalProgress SavingGoal(Guid bucketId) => _savings.GoalProgress(Account, bucketId);
    public string? SavingBucketRemovalBlocker(Guid id) => Account.SavingCategoryRemovalBlocker(id);

    public string MemberName(Guid memberId) =>
        Account.Members.FirstOrDefault(m => m.UserId == memberId)?.DisplayName ?? "—";

    /// <summary>The real signed-in users on this account (the server-authoritative header members — owner + invited
    /// contributors). Excludes members that only exist inside the imported snapshot (no real user behind them).</summary>
    public IReadOnlyList<MemberDto> RealUsers =>
        _summaries.ElementAtOrDefault(_accountIndex)?.Members ?? [];

    /// <summary>True when this member id belongs to a real signed-in user (vs a snapshot-imported placeholder).</summary>
    public bool IsRealUser(Guid memberId) => RealUsers.Any(m => m.UserId == memberId);

    // Member profile pictures (server-stored), loaded per account.
    private Guid _avatarsAccountId;
    private Dictionary<Guid, string> _memberAvatars = [];

    /// <summary>The member's profile picture (data-URL), or null to fall back to initials.</summary>
    public string? MemberAvatar(Guid memberId) =>
        _memberAvatars.TryGetValue(memberId, out var v) ? v : null;

    private async Task RefreshMemberAvatarsAsync(Guid accountId)
    {
        try
        {
            var avatars = await api.GetAccountAvatarsAsync(accountId);
            if (_avatarsAccountId == accountId) { _memberAvatars = avatars; Changed?.Invoke(); }
        }
        catch { /* best effort — fall back to initials */ }
    }

    /// <summary>Drop cached member avatars (e.g. after the signed-in user changes their own picture).</summary>
    public void InvalidateMemberAvatars() => _avatarsAccountId = Guid.Empty;

    public IReadOnlyList<(SavingCategory Bucket, Money Total)> SavingBuckets =>
        Account.SavingCategories
            .Select(b => (b, _savings.ForBucket(Account, Period, b.Id).AccumulatedTotal))
            .ToList();

    public string SavingBucketName(Guid id) => FindSavingBucket(id)?.Name ?? "—";

    /// <summary>This period's manual "Add to savings" deposits, newest first (editable/removable).</summary>
    public IReadOnlyList<SavingAllocation> SavingDepositsThisPeriod =>
        Period.ManualSavingDeposits().OrderByDescending(a => a.Date).ToList();

    public SavingAllocation? FindSavingDeposit(Guid id) =>
        Period.ManualSavingDeposits().FirstOrDefault(a => a.Id == id);

    /// <summary>This period's savings spendings (money matured into a budget, or moved between buckets), newest first.</summary>
    public IReadOnlyList<SavingAllocation> SavingMovementsThisPeriod =>
        Period.SavingMovements().OrderByDescending(a => a.Date).ToList();

    public SavingAllocation? FindSavingMovement(Guid id) =>
        Period.SavingMovements().FirstOrDefault(a => a.Id == id);

    /// <summary>A human-readable destination for a savings movement row (a budget category, or another bucket).</summary>
    public string SavingMovementTarget(SavingAllocation movement)
    {
        if (movement.BudgetCategoryId is { } categoryId)
            return $"{SavingBucketName(movement.SavingCategoryId)} → {CategoryName(categoryId)} (budget)";
        if (movement.TransferPairId is { } pairId)
        {
            var toId = Period.SavingAllocations
                .Where(a => a.TransferPairId == pairId && !a.Amount.IsNegative)
                .Select(a => a.SavingCategoryId)
                .FirstOrDefault();
            return $"{SavingBucketName(movement.SavingCategoryId)} → {SavingBucketName(toId)} (bucket)";
        }
        return SavingBucketName(movement.SavingCategoryId);
    }

    public Task EditSavingMovement(Guid allocationId, decimal amount)
    {
        Period.EditSavingMovement(allocationId, Money(amount));
        return SaveAsync();
    }

    public Task RemoveSavingMovement(Guid allocationId)
    {
        Period.RemoveSavingMovement(allocationId);
        return SaveAsync();
    }

    public IReadOnlyList<AccountMember> Members => Account.Members;
    public Contribution? ContributionFor(Guid memberId) =>
        Period.Contributions.FirstOrDefault(c => c.MemberId == memberId);

    /// <summary>Who the current actions are attributed to — the signed-in user (a member of the account).</summary>
    private Guid CurrentMemberId => auth.UserId;

    // --- Contribution categories + itemized deposits ----------------------
    public IReadOnlyList<ContributionCategory> ContributionCategories => Account.ContributionCategories;
    public string ContributionCategoryName(Guid id) =>
        Account.FindContributionCategory(id)?.Name ?? "—";
    public string? ContributionCategoryRemovalBlocker(Guid id) => Account.ContributionCategoryRemovalBlocker(id);

    /// <summary>This period's real member deposits (excludes the carryover sentinel), newest first.</summary>
    public IReadOnlyList<Contribution> ContributionsThisPeriod =>
        Period.Contributions.Where(c => c.MemberId != Period.CarryoverSource)
            .OrderByDescending(c => c.Date).ToList();

    public Contribution? FindContribution(Guid id) => Period.FindContribution(id);

    // --- Commands ---------------------------------------------------------

    /// <summary>Whether a fund is currently synced to a bank account (its balance is externally authoritative).</summary>
    public bool FundIsSynced(Guid fundId) => _account?.Funds.FirstOrDefault(f => f.Id == fundId)?.IsSynced ?? false;

    /// <summary>The account's synced fund (the one mirroring the linked bank account), or empty if none is marked.
    /// Bank-imported records route here automatically. First synced fund wins if several are marked.</summary>
    public Guid SyncedFundId => _account?.Funds.FirstOrDefault(f => f.IsSynced)?.Id ?? Guid.Empty;
    public bool HasSyncedFund => SyncedFundId != Guid.Empty;
    public string SyncedFundName => HasSyncedFund ? FundName(SyncedFundId) : "";

    /// <summary>Funds the user may target manually (expenses/transfers/deposits) — synced funds are excluded;
    /// they're driven only by the bank import flow.</summary>
    public IReadOnlyList<Fund> SelectableFunds => Account.RootFunds.Where(f => !f.IsSynced).ToList();

    public Task AddExpense(Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date, bool onBehalfOfOtherAccount = false)
    {
        var expense = new Expense(categoryId, Money(amount), date, CurrentMemberId, fundId, note,
            onBehalfOfOtherAccount: onBehalfOfOtherAccount);
        expense.SetFundSynced(FundIsSynced(fundId));   // synced funds aren't debited (real bank balance handles it)
        Period.AddExpense(expense);
        return SaveAsync();
    }

    public async Task EditExpense(Guid expenseId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date)
    {
        var before = Period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        var edited = Period.EditExpense(expenseId, categoryId, Money(amount), fundId, note, date);
        edited.SetFundSynced(FundIsSynced(fundId));   // recompute at edit time (moving to/from a synced fund)
        await SaveAsync();
        // Editing a settlement-destination expense mirrors the new amount back to the source expense.
        if (before is { IsSettlementDestination: true, SettlementId: { } sid, SettledFromAccountId: { } sourceAccount })
            await SyncSourceSettlementAmount(sourceAccount, sid, amount);
    }

    public async Task RemoveExpense(Guid expenseId)
    {
        var before = Period.Expenses.FirstOrDefault(e => e.Id == expenseId);
        Period.RemoveExpense(expenseId);
        await SaveAsync();
        // Removing one side of a settlement reverses the other: deleting the source drops the destination expense;
        // deleting the destination un-settles the source (restores its full amount).
        if (before is { IsSettlementSource: true, SettledToAccountId: { } destAccount, SettlementId: { } sid })
            await RemoveLinkedSettlementExpense(destAccount, sid);
        else if (before is { IsSettlementDestination: true, SettledFromAccountId: { } sourceAccount, SettlementId: { } sid2 })
            await SyncSourceSettlementAmount(sourceAccount, sid2, 0m);
    }

    /// <summary>Record a deposit for the signed-in user, classified by category and attributed to a fund.</summary>
    public Task RecordDeposit(Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        var contribution = Period.Deposit(CurrentMemberId, Money(amount), categoryId, fundId, date);
        contribution.SetFundSynced(FundIsSynced(fundId));   // synced destination fund isn't credited here
        return SaveAsync();
    }

    /// <summary>Edit one of the signed-in user's own deposit rows.</summary>
    public Task EditDeposit(Guid contributionId, Guid categoryId, Guid fundId, decimal amount, DateOnly date)
    {
        EnsureOwnContribution(contributionId);
        Period.EditContribution(contributionId, Money(amount), categoryId, fundId, date);
        Period.FindContribution(contributionId)?.SetFundSynced(FundIsSynced(fundId));   // recompute at edit time
        return SaveAsync();
    }

    /// <summary>Remove one of the signed-in user's own deposit rows.</summary>
    public Task RemoveDeposit(Guid contributionId)
    {
        EnsureOwnContribution(contributionId);
        Period.RemoveContribution(contributionId);
        return SaveAsync();
    }

    /// <summary>True when the deposit belongs to the signed-in user (only they may edit/remove it).</summary>
    public bool CanHandleContribution(Contribution c) => c.MemberId == CurrentMemberId;

    private void EnsureOwnContribution(Guid contributionId)
    {
        var c = Period.FindContribution(contributionId);
        if (c is null || !CanHandleContribution(c))
            throw new InvalidOperationException("You can only change your own contributions.");
    }

    public async Task<Guid> AddContributionCategory(string name, string? icon = null)
    {
        var c = Account.AddContributionCategory(name);
        Account.SetContributionCategoryIcon(c.Id, icon);
        await SaveAsync();
        return c.Id;
    }

    public Task RenameContributionCategory(Guid id, string name)
    {
        Account.RenameContributionCategory(id, name);
        return SaveAsync();
    }

    /// <summary>Rename a contribution category and set its icon in one save.</summary>
    public Task SaveContributionCategory(Guid id, string name, string? icon)
    {
        Account.RenameContributionCategory(id, name);
        Account.SetContributionCategoryIcon(id, icon);
        return SaveAsync();
    }

    public string ContributionCategoryIcon(Guid id) =>
        CategoryIcons.Effective(Account.FindContributionCategory(id)?.Icon, Account.FindContributionCategory(id)?.Name);
    public string? ContributionCategoryStoredIcon(Guid id) => Account.FindContributionCategory(id)?.Icon;

    public Task RemoveContributionCategory(Guid id)
    {
        Account.RemoveContributionCategory(id);
        return SaveAsync();
    }

    public Task AllocateSaving(Guid savingCategoryId, decimal amount, string? note)
    {
        Period.AllocateToSavings(savingCategoryId, Money(amount), Today(), note, PriorSaved);
        return SaveAsync();
    }

    public Task EditSavingDeposit(Guid allocationId, decimal amount)
    {
        Period.EditSavingDeposit(allocationId, Money(amount), PriorSaved);
        return SaveAsync();
    }

    public Task RemoveSavingDeposit(Guid allocationId)
    {
        Period.RemoveSavingAllocation(allocationId);
        return SaveAsync();
    }

    public Task SpendFromSavings(Guid savingCategoryId, Guid categoryId, decimal amount, string? note)
    {
        Period.ConvertSavingToExpense(savingCategoryId, categoryId, Money(amount), Today(),
            CurrentMemberId, DefaultFundId, note);
        return SaveAsync();
    }

    public Task ConvertSavingToBudget(Guid savingCategoryId, Guid categoryId, decimal amount, string? note)
    {
        Period.ConvertSavingToBudget(savingCategoryId, categoryId, Money(amount), Today(), note);
        return SaveAsync();
    }

    /// <summary>Move earmarked money from one savings bucket to another (net-neutral).</summary>
    public Task MoveSavingToBucket(Guid fromBucketId, Guid toBucketId, decimal amount, string? note)
    {
        Period.TransferSavings(fromBucketId, toBucketId, Money(amount), Today(), note);
        return SaveAsync();
    }

    /// <summary>True during initial setup (only the first period exists) — when a bucket's pre-existing initial balance may be set.</summary>
    public bool CanSetInitialSavings => PeriodCount == 1;

    // Saving bucket CRUD
    public async Task<Guid> AddSavingBucket(string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon = null)
    {
        var bucket = Account.AddSavingCategory(name);
        Account.SetSavingCategoryIcon(bucket.Id, icon);
        if (goalAmount is > 0m)
            Account.ConfigureSavingGoal(bucket.Id, goalAmount, thresholdPercent / 100m, notifyOnMilestone);
        if (CanSetInitialSavings && initialAmount > 0m)
            Account.SetSavingInitialAmount(bucket.Id, initialAmount);
        await SaveAsync();
        return bucket.Id;
    }

    public Task SaveSavingBucket(Guid savingCategoryId, string name, decimal? goalAmount, decimal thresholdPercent, bool notifyOnMilestone, decimal initialAmount, string? icon = null)
    {
        Account.RenameSavingCategory(savingCategoryId, name);
        Account.SetSavingCategoryIcon(savingCategoryId, icon);
        Account.ConfigureSavingGoal(savingCategoryId, goalAmount is > 0m ? goalAmount : null, thresholdPercent / 100m, notifyOnMilestone);
        if (CanSetInitialSavings)
            Account.SetSavingInitialAmount(savingCategoryId, initialAmount);
        return SaveAsync();
    }

    public string SavingBucketIcon(Guid id) =>
        CategoryIcons.Effective(FindSavingBucket(id)?.Icon, FindSavingBucket(id)?.Name);
    public string? SavingBucketStoredIcon(Guid id) => FindSavingBucket(id)?.Icon;

    public decimal SavingInitialAmount(Guid savingCategoryId) => FindSavingBucket(savingCategoryId)?.InitialAmount ?? 0m;

    public Task RemoveSavingBucket(Guid savingCategoryId)
    {
        Account.RemoveSavingCategory(savingCategoryId);
        return SaveAsync();
    }

    // Fund CRUD + transfers
    public async Task<Guid> AddFund(string name, string? note = null, string? icon = null)
    {
        var fund = Account.AddFund(name);
        if (!string.IsNullOrWhiteSpace(note))
            Account.SetFundNote(fund.Id, note);
        Account.SetFundIcon(fund.Id, icon);
        await SaveAsync();
        return fund.Id;
    }

    public Task RenameFund(Guid fundId, string name)
    {
        Account.RenameFund(fundId, name);
        return SaveAsync();
    }

    public Task SetFundIcon(Guid fundId, string? icon)
    {
        Account.SetFundIcon(fundId, icon);
        return SaveAsync();
    }

    /// <summary>Toggle a fund's bank-synced flag (forward-only — see <see cref="Fund.IsSynced"/>).</summary>
    public Task SetFundSynced(Guid fundId, bool synced)
    {
        Account.SetFundSynced(fundId, synced);
        return SaveAsync();
    }

    /// <summary>Bind a fund to the account's bank connection (the synced fund). Exactly one fund can be bound, so
    /// any other synced fund is cleared. Sets the fund flag (snapshot) and records the binding on the connection.</summary>
    public async Task BindFundToBank(Guid fundId)
    {
        foreach (var f in Account.Funds.Where(f => f.IsSynced && f.Id != fundId).ToList())
            Account.SetFundSynced(f.Id, false);
        Account.SetFundSynced(fundId, true);
        await SaveAsync();
        await api.SetBankFundAsync(CurrentAccountId, fundId);
    }

    /// <summary>Unbind a fund from the bank connection (stops routing imports; existing entries keep their markers).</summary>
    public async Task UnbindFundFromBank(Guid fundId)
    {
        Account.SetFundSynced(fundId, false);
        await SaveAsync();
        await api.SetBankFundAsync(CurrentAccountId, null);
        try { await api.RecordConsentAsync("bank_sync", CurrentAccountId, granted: false); } catch { /* audit best-effort */ }
    }

    public string FundIcon(Guid fundId) =>
        CategoryIcons.Effective(Account.FindFund(fundId)?.Icon, Account.FindFund(fundId)?.Name);
    public string? FundStoredIcon(Guid fundId) => Account.FindFund(fundId)?.Icon;

    public Task SetFundNote(Guid fundId, string? note)
    {
        Account.SetFundNote(fundId, note);
        return SaveAsync();
    }

    public bool FundHasOpeningBalance(Guid fundId) => Account.FundHasOpeningBalance(fundId);

    public Task RemoveFund(Guid fundId, Guid? moveOpeningBalancesTo = null)
    {
        Account.RemoveFund(fundId, moveOpeningBalancesTo);
        return SaveAsync();
    }

    public Task SetFundOpeningBalance(Guid fundId, decimal amount)
    {
        Period.SetInitialBalance(fundId, Money(amount));
        return SaveAsync();
    }

    public Task TransferFunds(Guid fromFundId, Guid toFundId, decimal amount, string? note)
    {
        var transfer = Period.TransferFunds(fromFundId, toFundId, Money(amount), Today(), note);
        transfer.SetSyncedSides(FundIsSynced(fromFundId), FundIsSynced(toFundId));   // synced sides aren't moved
        return SaveAsync();
    }

    public FundTransfer? FindFundTransfer(Guid id) => Period.FundTransfers.FirstOrDefault(t => t.Id == id);

    public Task EditFundTransfer(Guid id, Guid fromFundId, Guid toFundId, decimal amount, string? note)
    {
        var transfer = Period.EditFundTransfer(id, fromFundId, toFundId, Money(amount), note);
        transfer.SetSyncedSides(FundIsSynced(fromFundId), FundIsSynced(toFundId));
        return SaveAsync();
    }

    public Task RemoveFundTransfer(Guid id)
    {
        Period.RemoveFundTransfer(id);
        return SaveAsync();
    }

    // --- Cross-account transfers (money out -> a contribution in another account) ---

    /// <summary>Other accounts the money could be sent to: the user's other accounts in the same currency.</summary>
    public IReadOnlyList<AccountSummaryDto> TransferableAccounts =>
        _summaries.Where(a => a.Id != CurrentAccountId && a.Currency == Currency).ToList();

    public string AccountName(Guid accountId) =>
        _summaries.FirstOrDefault(a => a.Id == accountId)?.Name ?? "another account";

    /// <summary>Download the current account as an .xlsx (one sheet per period). Returns the file bytes + name.</summary>
    public Task<(byte[] Bytes, string FileName)> ExportCurrentAccountAsync() => api.ExportAccountAsync(CurrentAccountId);

    public ExternalTransfer? FindExternalTransfer(Guid id) =>
        Period.ExternalTransfers.FirstOrDefault(t => t.Id == id);

    /// <summary>The (root) funds of another account, for picking a transfer/settlement destination fund. Uses the
    /// warm cache when available, else fetches and deserializes the snapshot (read-only — not cached here).</summary>
    public async Task<IReadOnlyList<Fund>> LoadAccountFundsAsync(Guid accountId)
    {
        if (accountId == Guid.Empty) return [];
        if (_cache.TryGetValue(accountId, out var hit)) return hit.Account.RootFunds.ToList();
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (string.IsNullOrEmpty(snapshot.Payload)) return [];
        return AccountSnapshotSerializer.Deserialize(snapshot.Payload).RootFunds.ToList();
    }

    /// <summary>
    /// Send money from one of this account's funds to another account. The source records a real outflow
    /// (lowering the fund and the closing balance); the destination's current period receives it as a deposit
    /// from the signed-in user into the chosen fund. Two snapshots are pushed — this account's, then the destination's.
    /// </summary>
    public async Task TransferToAccount(Guid destinationAccountId, Guid fromFundId, decimal amount, string? note, Guid destinationFundId = default)
    {
        if (amount <= 0m) return;
        var destination = _summaries.FirstOrDefault(a => a.Id == destinationAccountId)
            ?? throw new InvalidOperationException("Destination account not found.");
        if (destination.Currency != Currency)
            throw new InvalidOperationException("Both accounts must use the same currency.");

        // 1) Record the outflow on this account and push it. A synced source fund keeps its real balance, so the
        //    outflow is informational only (marker true) — the row still shows what happened.
        var outflow = Period.TransferOut(fromFundId, Money(amount), Today(), destinationAccountId, note, PriorSaved);
        outflow.SetFundSynced(FundIsSynced(fromFundId));
        await SaveAsync();

        // 2) Load the destination, deposit into its current period for the signed-in user, and push it. Each side
        //    carries its own marker based on its own fund, so only the unsynced side actually moves (no double count).
        var snapshot = await api.GetSnapshotAsync(destinationAccountId);
        if (string.IsNullOrEmpty(snapshot.Payload))
            throw new InvalidOperationException($"Open “{destination.Name}” once before transferring into it.");
        var destAccount = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        var destPeriod = destAccount.CurrentPeriod
            ?? throw new InvalidOperationException($"“{destination.Name}” has no open period to receive the transfer.");
        var destFundId = ResolveDestinationFund(destAccount, destinationFundId);
        var destDeposit = destPeriod.Deposit(auth.UserId, new Money(amount, destAccount.Currency), fundId: destFundId, date: Today());
        destDeposit.SetFundSynced(destAccount.Funds.FirstOrDefault(f => f.Id == destFundId)?.IsSynced ?? false);
        var payload = AccountSnapshotSerializer.Serialize(destAccount);
        await api.SaveSnapshotAsync(destinationAccountId, new SaveAccountRequest(payload, snapshot.Version));
        _cache.Remove(destinationAccountId); // its snapshot changed under us — drop so a switch refetches (feature 5)
    }

    /// <summary>The (root) funds and categories of another account, for the settle-onto-account pickers.</summary>
    public async Task<(IReadOnlyList<Fund> Funds, IReadOnlyList<Category> Categories)> LoadAccountStructureAsync(Guid accountId)
    {
        if (accountId == Guid.Empty) return ([], []);
        var account = _cache.TryGetValue(accountId, out var hit)
            ? hit.Account
            : await DeserializeAccountAsync(accountId);
        return account is null ? ([], []) : (account.RootFunds.ToList(), account.Categories.ToList());
    }

    private async Task<Account?> DeserializeAccountAsync(Guid accountId)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        return string.IsNullOrEmpty(snapshot.Payload) ? null : AccountSnapshotSerializer.Deserialize(snapshot.Payload);
    }

    /// <summary>
    /// Settle (or re-settle) a portion of an "on behalf of another account" expense onto another account: the
    /// chosen amount becomes that account's own expense (in the picked fund + category) and the source expense is
    /// reduced by that amount. The two are linked by a settlement id so edits/removals on either side keep the
    /// other in step. (Feature 1.)
    /// </summary>
    public async Task SettleExpenseToAccount(Guid sourceExpenseId, Guid destinationAccountId, Guid destinationFundId, Guid destinationCategoryId, decimal amount, string? note)
    {
        if (amount <= 0m) return;
        var source = Period.Expenses.FirstOrDefault(e => e.Id == sourceExpenseId)
            ?? throw new InvalidOperationException("Expense not found in this period.");
        var destination = _summaries.FirstOrDefault(a => a.Id == destinationAccountId)
            ?? throw new InvalidOperationException("Destination account not found.");
        if (destination.Currency != Currency)
            throw new InvalidOperationException("Both accounts must use the same currency.");
        if (Money(amount) > source.OriginalAmount)
            throw new InvalidOperationException($"You can settle at most {source.OriginalAmount}.");

        var settlementId = source.SettlementId ?? Guid.NewGuid();
        var settleNote = string.IsNullOrWhiteSpace(note) ? $"On behalf — from {Account.Name}" : note;
        var thisAccountId = CurrentAccountId;

        // 1) Create or update the linked destination expense.
        await MutateOtherAccountAsync(destinationAccountId, dest =>
        {
            var destPeriod = dest.CurrentPeriod
                ?? throw new InvalidOperationException($"“{destination.Name}” has no open period to receive the expense.");
            var categoryId = ResolveCategory(dest, destinationCategoryId);
            var fundId = ResolveDestinationFund(dest, destinationFundId);
            if (destPeriod.Expenses.FirstOrDefault(e => e.SettlementId == settlementId) is { } existing)
                destPeriod.RemoveExpense(existing.Id);
            destPeriod.AddExpense(new Expense(categoryId, new Money(amount, dest.Currency), Today(), auth.UserId, fundId,
                settleNote, settlementId: settlementId, settledFromAccountId: thisAccountId));
        });

        // 2) Reduce the source expense and tag the link.
        Period.SetSettlement(sourceExpenseId, settlementId, destinationAccountId, Money(amount));
        await SaveAsync();
    }

    /// <summary>Undo a settlement from the source side: remove the linked destination expense and restore the source's full amount.</summary>
    public async Task UnsettleExpense(Guid sourceExpenseId)
    {
        var source = Period.Expenses.FirstOrDefault(e => e.Id == sourceExpenseId);
        if (source is not { IsSettlementSource: true, SettledToAccountId: { } destAccount, SettlementId: { } sid }) return;
        await RemoveLinkedSettlementExpense(destAccount, sid);
        Period.SetSettlement(sourceExpenseId, sid, destAccount, Money(0));
        await SaveAsync();
    }

    /// <summary>Mirror a new settled amount onto the source expense in another account (0 un-settles it).</summary>
    private Task SyncSourceSettlementAmount(Guid sourceAccountId, Guid settlementId, decimal newAmount) =>
        MutateOtherAccountAsync(sourceAccountId, source =>
        {
            foreach (var p in source.Periods)
                if (p.Expenses.FirstOrDefault(e => e.SettlementId == settlementId && e.IsSettlementSource) is { } ex)
                {
                    p.SetSettlement(ex.Id, settlementId, CurrentAccountId, new Money(newAmount, source.Currency));
                    return;
                }
        });

    private Task RemoveLinkedSettlementExpense(Guid accountId, Guid settlementId) =>
        MutateOtherAccountAsync(accountId, account =>
        {
            foreach (var p in account.Periods)
                if (p.Expenses.FirstOrDefault(e => e.SettlementId == settlementId) is { } ex)
                {
                    p.RemoveExpense(ex.Id);
                    return;
                }
        });

    /// <summary>Load another account, apply a mutation, push it, and drop its cache entry so a switch refetches.</summary>
    private async Task MutateOtherAccountAsync(Guid accountId, Action<Account> mutate)
    {
        var snapshot = await api.GetSnapshotAsync(accountId);
        if (string.IsNullOrEmpty(snapshot.Payload))
            throw new InvalidOperationException("Open that account once before linking to it.");
        var account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
        mutate(account);
        var payload = AccountSnapshotSerializer.Serialize(account);
        await api.SaveSnapshotAsync(accountId, new SaveAccountRequest(payload, snapshot.Version));
        _cache.Remove(accountId);
    }

    private static Guid ResolveCategory(Account account, Guid requestedCategoryId)
    {
        if (requestedCategoryId != Guid.Empty && account.Categories.Any(c => c.Id == requestedCategoryId))
            return requestedCategoryId;
        return account.RootCategories.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("That account has no category to record the expense against.");
    }

    private static Guid ResolveDestinationFund(Account destAccount, Guid requestedFundId)
    {
        if (requestedFundId != Guid.Empty && destAccount.RootFunds.Any(f => f.Id == requestedFundId))
            return requestedFundId;
        // Prefer an unsynced fund — a synced fund's balance is bank-managed and shouldn't receive a manual deposit.
        return (destAccount.RootFunds.FirstOrDefault(f => !f.IsSynced) ?? destAccount.RootFunds.FirstOrDefault())?.Id ?? Guid.Empty;
    }

    public Task RemoveExternalTransfer(Guid id)
    {
        Period.RemoveExternalTransfer(id);
        return SaveAsync();
    }

    // --- Bank sync (Open Banking) -----------------------------------------
    // The server stages raw bank transactions; the client turns a confirmed one into a real domain expense
    // (the account body is client-owned) and then acks it so a later sync won't resurface it.

    public Task<BankSyncStatusDto> GetBankStatus() => api.GetBankStatusAsync(CurrentAccountId);

    public Task<List<BankInstitutionDto>> GetBankInstitutions(string country = "GB") =>
        api.GetBankInstitutionsAsync(CurrentAccountId, country);

    /// <summary>Begin linking: returns the bank's consent URL for the UI to navigate to.</summary>
    public async Task<string> StartBankLink(string institutionName, string country, string? logo = null)
    {
        var resp = await api.StartBankLinkAsync(CurrentAccountId, new StartBankLinkRequest(institutionName, country, logo));
        return resp.LinkUrl;
    }

    public Task SyncBank() => api.SyncBankAsync(CurrentAccountId);

    public Task<List<PendingBankTransactionDto>> GetPendingBankTransactions() =>
        api.GetPendingBankTransactionsAsync(CurrentAccountId);

    public Task<List<BankAccountDto>> GetBankAccounts() => api.GetBankAccountsAsync(CurrentAccountId);
    public Task SelectBankAccount(string bankAccountRef) => api.SelectBankAccountAsync(CurrentAccountId, bankAccountRef);

    /// <summary>Turn a staged bank transaction into an expense in the given category/fund, then mark it handled.</summary>
    public async Task ConfirmBankTransaction(string externalId, Guid categoryId, decimal amount, Guid fundId, string? note, DateOnly date)
    {
        await AddExpense(categoryId, amount, fundId, note, date);
        await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
    }

    public Task DismissBankTransaction(string externalId) =>
        api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: false);

    /// <summary>Turn a bank money-in into a movement into the synced fund: the destination is the synced fund
    /// (not credited — the real balance handles it); the <paramref name="source"/> ("fund:{id}" or
    /// "contributor:{id}") is where it came from and is the side that actually moves. Then acks the row.</summary>
    public async Task ConfirmBankMoneyIn(string externalId, string source, decimal amount, string? note, DateOnly date)
    {
        if (!HasSyncedFund) throw new InvalidOperationException("Mark a fund as synced to your bank first (Edit fund).");
        var parts = (source ?? "").Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var targetId))
            throw new InvalidOperationException("Pick where this money came from.");

        if (parts[0] == "fund")
        {
            if (targetId == SyncedFundId) throw new InvalidOperationException("The source can't be the synced fund itself.");
            var transfer = Period.TransferFunds(targetId, SyncedFundId, Money(amount), date, note);
            transfer.SetSyncedSides(FundIsSynced(targetId), toSynced: true);   // synced destination isn't credited
        }
        else if (parts[0] == "contributor")
        {
            var deposit = Period.Deposit(targetId, Money(amount), fundId: SyncedFundId, date: date);
            deposit.SetFundSynced(true);   // counts as a contribution, but the synced fund isn't credited
        }
        else throw new InvalidOperationException("Unknown money-in source.");

        await SaveAsync();
        await api.AckBankTransactionAsync(CurrentAccountId, externalId, confirmed: true);
    }

    // --- Consent (audit-logged) -------------------------------------------
    public Task RecordConsent(string scope, Guid? accountId) => api.RecordConsentAsync(scope, accountId, granted: true);
    public Task WithdrawConsent(string scope, Guid? accountId) => api.RecordConsentAsync(scope, accountId, granted: false);

    /// <summary>Drop the current account's bank connection so it can be linked again. Withdraws link + sync consent.</summary>
    public async Task DisconnectBank()
    {
        var id = CurrentAccountId;
        await api.DisconnectBankAsync(id);
        try { await api.RecordConsentAsync("bank_sync", id, granted: false); await api.RecordConsentAsync("bank_link", id, granted: false); }
        catch { /* audit best-effort */ }
    }

    /// <summary>Re-open handled bank rows in a date range (e.g. after a period is deleted) so they resurface.</summary>
    public Task ResetBankRange(DateOnly from, DateOnly to) => api.ResetBankRangeAsync(CurrentAccountId, from, to);

    public Task<List<BankMappingDto>> GetBankMappings() => api.GetBankMappingsAsync(CurrentAccountId);
    public Task SetBankMapping(string description, string kind, Guid targetId) =>
        api.SetBankMappingAsync(CurrentAccountId, description, kind, targetId);
    public Task RemoveBankMapping(string description) => api.RemoveBankMappingAsync(CurrentAccountId, description);

    /// <summary>Normalize a bank description to the same key the server matches rules against (MatchKeyOf).</summary>
    public static string BankMatchKey(string description) =>
        string.Join(' ', (description ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public Task ReschedulePeriod(DateOnly from, DateOnly to)
    {
        Account.ReschedulePeriod(Period, from, to);
        return SaveAsync();
    }

    // Category CRUD
    public async Task<Guid> AddCategory(string name, Guid? parentId, string? icon = null)
    {
        var category = Account.AddCategory(name, parentId, icon);
        await SaveAsync();
        return category.Id;
    }

    public Task RenameCategory(Guid categoryId, string name)
    {
        Account.RenameCategory(categoryId, name);
        return SaveAsync();
    }

    /// <summary>Rename a category and set its icon in one save.</summary>
    public Task EditCategory(Guid categoryId, string name, string? icon)
    {
        Account.RenameCategory(categoryId, name);
        Account.SetCategoryIcon(categoryId, icon);
        return SaveAsync();
    }

    /// <summary>The icon to show for a category — its explicit choice, or one guessed from the name.</summary>
    public string CategoryIcon(Guid categoryId) => CategoryIcons.Effective(Account.FindCategory(categoryId));

    /// <summary>The category's explicitly-stored icon (null when none) — for pre-selecting the edit picker.</summary>
    public string? CategoryStoredIcon(Guid categoryId) => Account.FindCategory(categoryId)?.Icon;

    public Task RemoveCategory(Guid categoryId)
    {
        Account.RemoveCategory(categoryId);
        return SaveAsync();
    }

    // Budget CRUD
    public Task SaveBudget(Guid categoryId, decimal amount, decimal thresholdPercent, bool notifyEvery)
    {
        Period.SetBudget(categoryId, Money(amount), thresholdPercent / 100m, notifyEvery, PriorSaved);
        return SaveAsync();
    }

    public Task RemoveBudget(Guid categoryId)
    {
        Period.RemoveBudget(categoryId);
        return SaveAsync();
    }

    /// <summary>Remove the latest period and make the previous one active again.</summary>
    public Task RemoveLatestPeriod()
    {
        Account.RemoveLatestPeriod();
        _selectedIndex = Account.Periods.Count - 1;
        return SaveAsync();
    }

    /// <summary>
    /// Start the next period. The caller passes each top-level fund's real current balance, which becomes the
    /// new period's opening balance. That carried money is immediately allocatable (opening balances count toward
    /// what you can budget/save), so there's no separate carryover entry — what you actually have is what you have.
    /// </summary>
    public Task StartNextPeriod(bool copyBudgets, IReadOnlyDictionary<Guid, decimal> realFundOpenings, bool adjustBudgets = false)
    {
        var previous = Account.CurrentPeriod!;
        previous.Close();

        var from = previous.To.AddDays(1);
        var to = from.AddMonths(1).AddDays(-1);
        var next = Account.StartPeriod(from, to, copyBudgets, adjustBudgets && copyBudgets);

        foreach (var f in Account.RootFunds)
        {
            var amount = Money(realFundOpenings.TryGetValue(f.Id, out var v) ? v : 0m);
            next.SetInitialBalance(f.Id, amount);
        }

        _selectedIndex = Account.Periods.Count - 1;
        return SaveAsync();
    }

    // --- Invitations ------------------------------------------------------

    public IReadOnlyList<InvitationDto> PendingInvitations => _pendingInvitations;
    public int PendingInvitationCount => _pendingInvitations.Count;

    public async Task RefreshInvitationsAsync()
    {
        _pendingInvitations = await api.GetPendingInvitationsAsync();
        Changed?.Invoke();
    }

    public Task InviteToCurrentAccount(string username) => api.InviteAsync(CurrentAccountId, username);

    public async Task AcceptInvitation(Guid invitationId)
    {
        var accountId = await api.AcceptInvitationAsync(invitationId);
        await sync.SubscribeAsync(accountId);
        _summaries = await api.GetAccountsAsync();
        _accountIndex = Math.Max(0, _summaries.FindIndex(a => a.Id == accountId));
        await LoadSelectedAccountAsync();
        await RefreshInvitationsAsync();
        Changed?.Invoke();
    }

    public async Task DeclineInvitation(Guid invitationId)
    {
        await api.DeclineInvitationAsync(invitationId);
        await RefreshInvitationsAsync();
    }

    // --- Live sync handlers (fire on a background thread) ------------------

    private async void OnAccountChanged(AccountChangedEvent e)
    {
        if (e.ChangedByUserId == auth.UserId) return; // our own change is already applied locally + cached

        _cache.Remove(e.AccountId); // a contributor changed it — drop the stale entry (re-fetched on next view)

        if (_account is null || e.AccountId != _account.Id) return; // not the account in view: lazy refresh later
        try
        {
            var snapshot = await api.GetSnapshotAsync(e.AccountId);
            if (!string.IsNullOrEmpty(snapshot.Payload))
            {
                _version = snapshot.Version;
                _account = AccountSnapshotSerializer.Deserialize(snapshot.Payload);
                ReconcileHeader(_account, _summaries[_accountIndex]);
                _cache[e.AccountId] = new CachedAccount(_account, _version);
                _selectedIndex = Math.Min(_selectedIndex, _account.Periods.Count - 1);
                Changed?.Invoke();
            }
        }
        catch { /* a transient reload failure shouldn't crash the UI */ }
    }

    /// <summary>On reconnect the hub's group memberships are gone and changes during the outage were missed, so
    /// drop the whole cache, re-join every account's channel, and refresh the one in view from the server.</summary>
    private async void OnReconnected()
    {
        try
        {
            _cache.Clear();
            await SubscribeAllAsync();
            if (_account is not null) { await LoadSelectedAccountAsync(forceRefresh: true); Changed?.Invoke(); }
        }
        catch { /* best effort */ }
    }

    /// <summary>Join the live channel for every account the user belongs to, so AccountChanged fires for all of
    /// them (and can invalidate their cache entries) — not just the one currently open.</summary>
    private async Task SubscribeAllAsync()
    {
        foreach (var s in _summaries)
        {
            try { await sync.SubscribeAsync(s.Id); } catch { /* best effort */ }
        }
    }

    private async void OnInvitationReceived(InvitationReceivedEvent e)
    {
        try { await RefreshInvitationsAsync(); } catch { /* best effort */ }
    }

    // --- Helpers ----------------------------------------------------------

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

    private static bool NameEquals(string existing, string candidate) =>
        string.Equals(existing.Trim(), candidate?.Trim(), StringComparison.OrdinalIgnoreCase);

    private Task SaveAsync()
    {
        Changed?.Invoke();
        return PushSnapshotAsync();
    }

    /// <summary>A fresh, usable account body: starter categories/buckets, default funds, and the current month's period.</summary>
    private static void SeedStarterBody(Account account)
    {
        foreach (var (name, icon) in new[] { ("Food", "🍽️"), ("Bills", "💡"), ("Transport", "🚗"), ("Other", "🏷️") })
            account.AddCategory(name, icon: icon);
        account.AddSavingCategory("General");
        foreach (var c in new[] { "Salary", "Other" })
            account.AddContributionCategory(c);
        account.AddDefaultFunds();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = new DateOnly(today.Year, today.Month, 1);
        account.StartPeriod(from, from.AddMonths(1).AddDays(-1));
    }
}
