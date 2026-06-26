using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Savings;

namespace FinApp.Domain.Periods;

/// <summary>
/// A budgeting period (from → to) inside an account. Owns its opening balances, member
/// contributions, budgets, expense ledger and savings movements. All money is in the account currency.
/// </summary>
public sealed class Period : Entity
{
    private readonly List<InitialBalance> _initialBalances = [];
    private readonly List<Contribution> _contributions = [];
    private readonly List<Budget> _budgets = [];
    private readonly List<Expense> _expenses = [];
    private readonly List<SavingAllocation> _savingAllocations = [];
    private readonly List<FundTransfer> _fundTransfers = [];
    private readonly List<ExternalTransfer> _externalTransfers = [];

    public string Currency { get; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public PeriodStatus Status { get; private set; } = PeriodStatus.Open;

    /// <summary>
    /// Vestigial: the old signed "From previous period" leftover. Carried money now simply sits in the opening
    /// fund balances (which are allocatable), so this is always zero. Retained only so existing persisted
    /// snapshots/rows keep deserializing; not used in any calculation.
    /// </summary>
    public Money CarriedIn { get; private set; }

    public IReadOnlyList<InitialBalance> InitialBalances => _initialBalances;
    public IReadOnlyList<Contribution> Contributions => _contributions;
    public IReadOnlyList<Budget> Budgets => _budgets;
    public IReadOnlyList<Expense> Expenses => _expenses;
    public IReadOnlyList<SavingAllocation> SavingAllocations => _savingAllocations;
    public IReadOnlyList<FundTransfer> FundTransfers => _fundTransfers;
    public IReadOnlyList<ExternalTransfer> ExternalTransfers => _externalTransfers;

    public Period(string currency, DateOnly from, DateOnly to)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        if (to < from)
            throw new ArgumentException("Period end cannot be before start.", nameof(to));
        Currency = currency.ToUpperInvariant();
        From = from;
        To = to;
        CarriedIn = Money.Zero(Currency);
    }

    /// <summary>Change the period's date range. Account-level rescheduling cascades this to later periods.</summary>
    public void Reschedule(DateOnly from, DateOnly to)
    {
        if (to < from)
            throw new ArgumentException("Period end cannot be before start.", nameof(to));
        From = from;
        To = to;
    }

    public int LengthInDays => To.DayNumber - From.DayNumber;

    // --- Opening balances -------------------------------------------------

    public void SetInitialBalance(Guid fundId, Money amount, bool informative = false)
    {
        EnsureCurrency(amount);
        var existing = _initialBalances.FirstOrDefault(b => b.FundId == fundId);
        if (existing is null)
            _initialBalances.Add(new InitialBalance(fundId, amount, informative));
        else
            existing.Set(amount, informative);
    }

    /// <summary>Drop a fund's opening-balance row (used when the fund is removed).</summary>
    public void RemoveInitialBalance(Guid fundId) =>
        _initialBalances.RemoveAll(b => b.FundId == fundId);

    /// <summary>Real opening total — excludes sub-fund (informative) balances, which only break down their parent.</summary>
    public Money InitialTotal => Sum(_initialBalances.Where(b => !b.Informative).Select(b => b.Amount));

    /// <summary>A fund's opening balance for this period (0 if none), informative or not.</summary>
    public Money OpeningBalanceOf(Guid fundId) =>
        _initialBalances.FirstOrDefault(b => b.FundId == fundId)?.Amount ?? Money.Zero(Currency);

    /// <summary>
    /// Move this period's opening balance from one fund to another (used when a fund is removed). The
    /// period total is preserved, so reconciliation is unaffected. No-op when the source has no opening balance.
    /// </summary>
    public void MoveInitialBalance(Guid fromFundId, Guid toFundId)
    {
        var source = _initialBalances.FirstOrDefault(b => b.FundId == fromFundId);
        if (source is null) return;
        var amount = source.Amount;
        _initialBalances.Remove(source);
        if (amount.IsZero) return;
        SetInitialBalance(toFundId, (FindInitialBalance(toFundId)?.Amount ?? Money.Zero(Currency)) + amount);
    }

    private InitialBalance? FindInitialBalance(Guid fundId) => _initialBalances.FirstOrDefault(b => b.FundId == fundId);

    // --- Fund transfers & per-fund position -------------------------------

    /// <summary>Record a transfer of money from one fund to another. Total-preserving — see <see cref="FundTransfer"/>.
    /// Capped at the source fund's current balance so a fund can't go negative.</summary>
    public FundTransfer TransferFunds(Guid fromFundId, Guid toFundId, Money amount, DateOnly date, string? note = null)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        var transfer = new FundTransfer(fromFundId, toFundId, amount, date, note); // validates funds differ + amount > 0
        if (amount > FundBalance(fromFundId))
            throw new InvalidOperationException($"That fund only holds {FundBalance(fromFundId)} to move.");
        _fundTransfers.Add(transfer);
        return transfer;
    }

    /// <summary>Replace a transfer's funds/amount/note (keeps its original date). Removes the old entry and adds a fresh one.</summary>
    public FundTransfer EditFundTransfer(Guid transferId, Guid fromFundId, Guid toFundId, Money amount, string? note)
    {
        EnsureOpen();
        var old = _fundTransfers.FirstOrDefault(t => t.Id == transferId)
            ?? throw new InvalidOperationException("Transfer not found in this period.");
        _fundTransfers.Remove(old);
        return TransferFunds(fromFundId, toFundId, amount, old.Date, note);
    }

    public void RemoveFundTransfer(Guid transferId)
    {
        EnsureOpen();
        var transfer = _fundTransfers.FirstOrDefault(t => t.Id == transferId)
            ?? throw new InvalidOperationException("Transfer not found in this period.");
        _fundTransfers.Remove(transfer);
    }

    /// <summary>
    /// A fund's position in this period: opening balance + transfers in − transfers out − spending from it
    /// − money sent out to other accounts. Contributions aren't fund-attributed, so this is a per-fund
    /// spending position, not a share of the (contribution-inclusive) closing balance.
    /// </summary>
    public Money FundBalance(Guid fundId)
    {
        var opening = Sum(_initialBalances.Where(b => b.FundId == fundId).Select(b => b.Amount));
        var transfersIn = Sum(_fundTransfers.Where(t => t.ToFundId == fundId).Select(t => t.Amount));
        var transfersOut = Sum(_fundTransfers.Where(t => t.FromFundId == fundId).Select(t => t.Amount));
        var spent = Sum(_expenses.Where(e => e.FundId == fundId).Select(e => e.Amount));
        var sentOut = Sum(_externalTransfers.Where(t => t.FundId == fundId).Select(t => t.Amount));
        var depositsIn = Sum(_contributions.Where(c => c.MemberId != CarryoverSource && c.FundId == fundId).Select(c => c.Paid));
        return opening + transfersIn + depositsIn - transfersOut - spent - sentOut;
    }

    // --- Transfers to other accounts --------------------------------------

    /// <summary>
    /// Send money out of a fund to another account (where it arrives as a member contribution). A real
    /// outflow: it lowers the fund's position and the period's closing balance. Net-of-account, not
    /// net-neutral. The matching deposit is recorded separately in the destination account.
    /// </summary>
    public ExternalTransfer TransferOut(Guid fundId, Money amount, DateOnly date, Guid? toAccountId = null, string? note = null, Money? priorSaved = null)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        if (amount > FundBalance(fundId))
            throw new InvalidOperationException(
                $"That fund only holds {FundBalance(fundId)}; move money into it from another fund first.");
        var available = AvailableToTransferOutAfter(priorSaved ?? Money.Zero(Currency));
        if (amount > available)
            throw new InvalidOperationException(
                $"Can't send more than the unreserved cash ({available}); the rest is earmarked for savings.");
        var transfer = new ExternalTransfer(fundId, amount, date, toAccountId, note);
        _externalTransfers.Add(transfer);
        return transfer;
    }

    /// <summary>
    /// The most that can be sent out to another account without going underwater: the cash actually in the
    /// account (<see cref="ExpectedClosingBalance"/>) minus what's already earmarked for savings. Unlike an
    /// expense (which may overspend), a discretionary transfer shouldn't break the savings earmark.
    /// </summary>
    public Money AvailableToTransferOut => AvailableToTransferOutAfter(Money.Zero(Currency));

    /// <summary>As <see cref="AvailableToTransferOut"/>, but counting <paramref name="priorSaved"/> (savings
    /// accumulated in earlier periods / initial balances) as earmarked too, so carried-over savings can't be sent out.</summary>
    public Money AvailableToTransferOutAfter(Money priorSaved)
    {
        var total = SavingsNetTotal + priorSaved;
        var earmarked = total.IsNegative ? Money.Zero(Currency) : total;
        var free = ExpectedClosingBalance - earmarked;
        return free.IsNegative ? Money.Zero(Currency) : free;
    }

    /// <summary>The most that can be sent out <b>from a specific fund</b>: the lower of what that fund actually
    /// holds and the account-wide unreserved cash (so neither the fund nor the savings earmark goes negative).</summary>
    public Money AvailableToTransferOutFromFund(Guid fundId) => AvailableToTransferOutFromFundAfter(fundId, Money.Zero(Currency));

    public Money AvailableToTransferOutFromFundAfter(Guid fundId, Money priorSaved)
    {
        var inFund = FundBalance(fundId);
        var freeCash = AvailableToTransferOutAfter(priorSaved);
        return inFund < freeCash ? inFund : freeCash;
    }

    public void RemoveExternalTransfer(Guid transferId)
    {
        EnsureOpen();
        var transfer = _externalTransfers.FirstOrDefault(t => t.Id == transferId)
            ?? throw new InvalidOperationException("External transfer not found in this period.");
        _externalTransfers.Remove(transfer);
    }

    /// <summary>Total money sent out to other accounts this period (reduces the closing balance).</summary>
    public Money ExternalOutTotal => Sum(_externalTransfers.Select(t => t.Amount));

    // --- Contributions ----------------------------------------------------

    /// <summary>
    /// Record a member's deposit, classified by <paramref name="categoryId"/> and attributed to
    /// <paramref name="fundId"/> (the money lands in that fund). Deposits with the same
    /// (member, category, fund) merge into one row; different combinations are separate rows.
    /// </summary>
    public Contribution Deposit(Guid memberId, Money amount, Guid categoryId = default, Guid fundId = default, DateOnly date = default)
    {
        EnsureCurrency(amount);
        var existing = _contributions.FirstOrDefault(c =>
            c.MemberId == memberId && c.CategoryId == categoryId && c.FundId == fundId);
        if (existing is null)
        {
            existing = new Contribution(memberId, Money.Zero(Currency), categoryId, fundId, date);
            _contributions.Add(existing);
        }
        existing.RecordPayment(amount);
        return existing;
    }

    public Contribution? FindContribution(Guid contributionId) =>
        _contributions.FirstOrDefault(c => c.Id == contributionId);

    /// <summary>Overwrite a deposit row's amount/category/fund/date (used when editing a deposit).</summary>
    public void EditContribution(Guid contributionId, Money amount, Guid categoryId, Guid fundId, DateOnly date)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        var contribution = FindContribution(contributionId)
            ?? throw new InvalidOperationException("Contribution not found in this period.");
        contribution.Update(amount, categoryId, fundId, date);
    }

    /// <summary>Remove a deposit row.</summary>
    public void RemoveContribution(Guid contributionId)
    {
        EnsureOpen();
        var contribution = FindContribution(contributionId)
            ?? throw new InvalidOperationException("Contribution not found in this period.");
        _contributions.Remove(contribution);
    }

    /// <summary>New member deposits this period. Excludes the "From previous period" carryover, which is held
    /// signed in <see cref="CarriedIn"/> rather than as a contribution row.</summary>
    public Money ContributionsPaidTotal => Sum(_contributions.Where(c => c.MemberId != CarryoverSource).Select(c => c.Paid));

    /// <summary>
    /// Sentinel "member"/"category" id once used for the automatic carryover contribution and the
    /// cover-shortfall savings movement. Carryover is no longer modelled, but the constant is retained so
    /// older persisted snapshots that still contain such rows continue to deserialize.
    /// </summary>
    public static readonly Guid CarryoverSource = new("00000000-0000-0000-0000-00000000ca11");

    // --- Budgets ----------------------------------------------------------

    public Budget AddBudget(Guid categoryId, Money allocated, decimal alertThreshold = 0.80m, bool notifyOnEveryExpense = false)
    {
        EnsureCurrency(allocated);
        if (_budgets.Any(b => b.CategoryId == categoryId))
            throw new InvalidOperationException("A budget already exists for this category in the period.");
        var budget = new Budget(categoryId, allocated, alertThreshold, notifyOnEveryExpense);
        _budgets.Add(budget);
        return budget;
    }

    public Budget? FindBudget(Guid categoryId) => _budgets.FirstOrDefault(b => b.CategoryId == categoryId);

    public void RemoveBudget(Guid categoryId)
    {
        var budget = FindBudget(categoryId)
            ?? throw new InvalidOperationException("No budget exists for this category in the period.");
        _budgets.Remove(budget);
    }

    /// <summary>
    /// Create or update a budget, enforcing the planning cap: budgets + savings can't exceed the money actually
    /// in the account (<see cref="ExpectedClosingBalance"/>). (Actual expenses are not capped — overspending
    /// is allowed.)
    /// </summary>
    public Budget SetBudget(Guid categoryId, Money allocated, decimal alertThreshold = 0.80m, bool notifyOnEveryExpense = false, Money? priorSaved = null)
    {
        EnsureCurrency(allocated);
        if (allocated.IsNegative)
            throw new ArgumentException("Allocated amount cannot be negative.", nameof(allocated));

        var existing = FindBudget(categoryId);
        var othersBudgeted = BudgetedTotal - (existing?.Allocated ?? Money.Zero(Currency));
        var totalSaved = SavingsNetTotal + (priorSaved ?? Money.Zero(Currency));
        if (othersBudgeted + allocated + totalSaved > ExpectedClosingBalance)
            throw new InvalidOperationException(
                $"Budgets + savings can't exceed the money in the account ({ExpectedClosingBalance}).");

        if (existing is null)
        {
            existing = new Budget(categoryId, allocated, alertThreshold, notifyOnEveryExpense);
            _budgets.Add(existing);
        }
        else
        {
            existing.SetAllocation(allocated);
            existing.Configure(alertThreshold, notifyOnEveryExpense);
        }
        return existing;
    }

    /// <summary>Total allocated across all budgets in the period.</summary>
    public Money BudgetedTotal => Sum(_budgets.Select(b => b.Allocated));

    // --- Expenses ---------------------------------------------------------

    public Expense AddExpense(Expense expense)
    {
        EnsureCurrency(expense.Amount);
        EnsureOpen();
        _expenses.Add(expense);
        return expense;
    }

    /// <summary>
    /// Remove an expense. If it was paid from savings (a saving→expense conversion), the matching
    /// negative drawdown is removed too, restoring the saving earmark so balances stay consistent.
    /// </summary>
    public void RemoveExpense(Guid expenseId)
    {
        EnsureOpen();
        var expense = _expenses.FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("Expense not found in this period.");
        _expenses.Remove(expense);
        _savingAllocations.RemoveAll(a => a.SourceExpenseId == expenseId);
    }

    /// <summary>
    /// Replace an expense's category/amount/fund/note/date (the ledger stays append-only — this removes the
    /// old entry and adds a fresh one, keeping its original member). Saving-funded expenses keep their
    /// savings link, re-syncing the drawdown to the new amount.
    /// </summary>
    public Expense EditExpense(Guid expenseId, Guid categoryId, Money amount, Guid fundId, string? note, DateOnly date)
    {
        EnsureOpen();
        var old = _expenses.FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("Expense not found in this period.");
        RemoveExpense(expenseId);
        return old.SourceSavingCategoryId is { } savingId
            ? ConvertSavingToExpense(savingId, categoryId, amount, date, old.MemberId, fundId, note)
            : AddExpense(new Expense(categoryId, amount, date, old.MemberId, fundId, note,
                onBehalfOfOtherAccount: old.OnBehalfOfOtherAccount,
                settlementId: old.SettlementId,
                settledToAccountId: old.SettledToAccountId,
                settledFromAccountId: old.SettledFromAccountId,
                settledAmount: old.SettledAmount));
    }

    /// <summary>
    /// Settle (or re-settle) a portion of an expense onto another account: reduce this expense to
    /// <c>original − settledAmount</c> and tag it with the settlement link. Passing a zero amount un-settles it
    /// (restores the full amount and clears the link). The matching destination expense is managed by the caller.
    /// </summary>
    public Expense SetSettlement(Guid expenseId, Guid settlementId, Guid toAccountId, Money settledAmount)
    {
        EnsureCurrency(settledAmount);
        EnsureOpen();
        var old = _expenses.FirstOrDefault(e => e.Id == expenseId)
            ?? throw new InvalidOperationException("Expense not found in this period.");
        var original = old.OriginalAmount;
        if (settledAmount.IsNegative || settledAmount > original)
            throw new InvalidOperationException($"You can settle between 0 and the expense amount ({original}).");

        _expenses.Remove(old);
        var settled = !settledAmount.IsZero;
        var updated = new Expense(old.CategoryId, original - settledAmount, old.Date, old.MemberId, old.FundId, old.Note,
            old.SourceSavingCategoryId, onBehalfOfOtherAccount: old.OnBehalfOfOtherAccount,
            settlementId: settled ? settlementId : null,
            settledToAccountId: settled ? toAccountId : null,
            settledAmount: settled ? settledAmount.Amount : 0m);
        _expenses.Add(updated);
        return updated;
    }

    public Money ExpensesTotal => Sum(_expenses.Select(e => e.Amount));

    // --- Savings ----------------------------------------------------------

    public SavingAllocation AllocateToSavings(Guid savingCategoryId, Money amount, DateOnly date, string? note = null, Money? priorSaved = null)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        if (amount.IsNegative)
            throw new ArgumentException("Use ConvertSavingToExpense to draw down savings.", nameof(amount));
        var max = MaxAdditionalSavingsAfter(priorSaved ?? Money.Zero(Currency));
        if (amount > max)
            throw new InvalidOperationException(
                $"Cannot save more than the money available after budgets ({max} available).");
        var allocation = new SavingAllocation(savingCategoryId, amount, date, note);
        _savingAllocations.Add(allocation);
        return allocation;
    }

    /// <summary>
    /// A plain "Add to savings" deposit: a positive, un-noted allocation not linked to an expense. (Carryover,
    /// transfers, budget moves and saving→expense drawdowns all carry a note or a source link, so they're excluded.)
    /// </summary>
    private static bool IsManualDeposit(SavingAllocation a) =>
        !a.Amount.IsNegative && !a.Amount.IsZero && a.SourceExpenseId is null
        && a.BudgetCategoryId is null && a.TransferPairId is null && string.IsNullOrEmpty(a.Note);

    /// <summary>This period's manual savings deposits (the ones a member can edit or remove).</summary>
    public IEnumerable<SavingAllocation> ManualSavingDeposits() => _savingAllocations.Where(IsManualDeposit);

    /// <summary>Remove a manual savings deposit.</summary>
    public void RemoveSavingAllocation(Guid allocationId)
    {
        EnsureOpen();
        var allocation = _savingAllocations.FirstOrDefault(a => a.Id == allocationId)
            ?? throw new InvalidOperationException("Savings deposit not found in this period.");
        if (!IsManualDeposit(allocation))
            throw new InvalidOperationException("Only a savings deposit can be removed here.");
        _savingAllocations.Remove(allocation);
    }

    /// <summary>Change the amount of a manual savings deposit (re-checks the savings cap; keeps its original date).</summary>
    public void EditSavingDeposit(Guid allocationId, Money newAmount, Money? priorSaved = null)
    {
        EnsureCurrency(newAmount);
        EnsureOpen();
        if (newAmount.IsNegative)
            throw new ArgumentException("Deposit amount cannot be negative.", nameof(newAmount));
        var old = _savingAllocations.FirstOrDefault(a => a.Id == allocationId)
            ?? throw new InvalidOperationException("Savings deposit not found in this period.");
        if (!IsManualDeposit(old))
            throw new InvalidOperationException("Only a savings deposit can be edited here.");

        _savingAllocations.Remove(old);
        var max = MaxAdditionalSavingsAfter(priorSaved ?? Money.Zero(Currency));
        if (newAmount > max)
        {
            _savingAllocations.Add(old); // restore before failing
            throw new InvalidOperationException(
                $"Cannot save more than the money available after budgets ({max} available).");
        }
        if (!newAmount.IsZero)
            _savingAllocations.Add(new SavingAllocation(old.SavingCategoryId, newAmount, old.Date));
    }

    /// <summary>
    /// Spend accumulated savings: records a real <see cref="Expense"/> against a budget category
    /// and a matching negative savings drawdown so the saving earmark and physical money both fall.
    /// </summary>
    public Expense ConvertSavingToExpense(
        Guid savingCategoryId,
        Guid categoryId,
        Money amount,
        DateOnly date,
        Guid memberId,
        Guid fundId,
        string? note = null)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        if (amount.IsNegative)
            throw new ArgumentException("Amount cannot be negative.", nameof(amount));

        var expense = new Expense(categoryId, amount, date, memberId, fundId, note, savingCategoryId);
        _expenses.Add(expense);
        _savingAllocations.Add(new SavingAllocation(savingCategoryId, -amount, date, note ?? "Saving spent", expense.Id));
        return expense;
    }

    /// <summary>
    /// Mature a saving into a spendable budget for this period: release the saving earmark and add the
    /// amount to a category's budget allocation (creating the budget if needed). No money physically
    /// moves until real expenses are recorded against the budget, so the period's closing balance —
    /// and therefore reconciliation with the next period — is unaffected.
    /// </summary>
    public void ConvertSavingToBudget(Guid savingCategoryId, Guid categoryId, Money amount, DateOnly date, string? note = null)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        if (amount.IsNegative)
            throw new ArgumentException("Amount cannot be negative.", nameof(amount));

        var budget = FindBudget(categoryId);
        if (budget is null)
            AddBudget(categoryId, amount);
        else
            budget.SetAllocation(budget.Allocated + amount);

        _savingAllocations.Add(new SavingAllocation(savingCategoryId, -amount, date, note ?? "Moved to budget", budgetCategoryId: categoryId));
    }

    /// <summary>
    /// Move earmarked money from one savings bucket to another. Net-neutral (the period's total savings
    /// don't change), so it isn't subject to the savings cap — only the per-bucket split shifts.
    /// </summary>
    public void TransferSavings(Guid fromSavingCategoryId, Guid toSavingCategoryId, Money amount, DateOnly date, string? note = null)
    {
        EnsureCurrency(amount);
        EnsureOpen();
        if (fromSavingCategoryId == toSavingCategoryId)
            throw new ArgumentException("Choose two different savings buckets.", nameof(toSavingCategoryId));
        if (amount.IsNegative || amount.IsZero)
            throw new ArgumentException("Transfer amount must be positive.", nameof(amount));

        var pairId = Guid.NewGuid();
        _savingAllocations.Add(new SavingAllocation(fromSavingCategoryId, -amount, date, note ?? "Moved to another bucket", transferPairId: pairId));
        _savingAllocations.Add(new SavingAllocation(toSavingCategoryId, amount, date, note ?? "Moved from another bucket", transferPairId: pairId));
    }

    /// <summary>
    /// This period's savings <i>spendings/movements</i> the user can review and undo: money matured into a
    /// budget, and bucket-to-bucket transfers (represented by their outgoing half). Plain "Add to savings"
    /// deposits and saving→expense drawdowns are excluded (those have their own edit paths).
    /// </summary>
    public IEnumerable<SavingAllocation> SavingMovements() =>
        _savingAllocations.Where(a => a.BudgetCategoryId is not null
            || (a.TransferPairId is not null && a.Amount.IsNegative));

    /// <summary>
    /// Undo a savings movement. A move-to-budget reduces the funded budget back down; a bucket transfer
    /// drops both halves. Pass either half's id for a transfer.
    /// </summary>
    public void RemoveSavingMovement(Guid allocationId)
    {
        EnsureOpen();
        var movement = _savingAllocations.FirstOrDefault(a => a.Id == allocationId)
            ?? throw new InvalidOperationException("Savings movement not found in this period.");

        if (movement.BudgetCategoryId is { } categoryId)
        {
            if (FindBudget(categoryId) is { } budget)
            {
                var reduced = budget.Allocated + movement.Amount; // movement.Amount is negative
                budget.SetAllocation(reduced.IsNegative ? Money.Zero(Currency) : reduced);
            }
            _savingAllocations.Remove(movement);
        }
        else if (movement.TransferPairId is { } pairId)
        {
            _savingAllocations.RemoveAll(a => a.TransferPairId == pairId);
        }
        else
        {
            throw new InvalidOperationException("Only a savings movement can be removed here.");
        }
    }

    /// <summary>Change the amount of a savings movement (remove + re-apply, keeping its kind, buckets and date).</summary>
    public void EditSavingMovement(Guid allocationId, Money newAmount)
    {
        EnsureCurrency(newAmount);
        EnsureOpen();
        if (newAmount.IsNegative || newAmount.IsZero)
            throw new ArgumentException("Movement amount must be positive.", nameof(newAmount));

        var movement = _savingAllocations.FirstOrDefault(a => a.Id == allocationId)
            ?? throw new InvalidOperationException("Savings movement not found in this period.");

        if (movement.BudgetCategoryId is { } categoryId)
        {
            var bucketId = movement.SavingCategoryId;
            var date = movement.Date;
            RemoveSavingMovement(allocationId);
            ConvertSavingToBudget(bucketId, categoryId, newAmount, date);
        }
        else if (movement.TransferPairId is { } pairId)
        {
            var halves = _savingAllocations.Where(a => a.TransferPairId == pairId).ToList();
            var fromId = halves.First(a => a.Amount.IsNegative).SavingCategoryId;
            var toId = halves.First(a => !a.Amount.IsNegative).SavingCategoryId;
            var date = movement.Date;
            RemoveSavingMovement(allocationId);
            TransferSavings(fromId, toId, newAmount, date);
        }
        else
        {
            throw new InvalidOperationException("Only a savings movement can be edited here.");
        }
    }

    /// <summary>Net amount set aside this period across all savings buckets (allocations minus drawdowns).</summary>
    public Money SavingsNetTotal => Sum(_savingAllocations.Select(a => a.Amount));

    /// <summary>
    /// The money you can plan/earmark with this period: the cash actually in the account
    /// (<see cref="ExpectedClosingBalance"/> = opening fund balances + new deposits − expenses − money sent out),
    /// less what's already committed to budgets. Opening fund balances <b>do</b> count — carried-over money simply
    /// sits there, so it's spendable without any separate carryover mechanism.
    /// </summary>
    public Money AvailableToSave => AvailableToSaveAfter(Money.Zero(Currency));

    /// <summary>As <see cref="AvailableToSave"/>, but reserving <paramref name="priorSaved"/> (savings accumulated
    /// in earlier periods / initial balances), so carried-over savings aren't offered up for re-allocation.</summary>
    public Money AvailableToSaveAfter(Money priorSaved) => ExpectedClosingBalance - BudgetedTotal - priorSaved;

    /// <summary>How much more can still be moved into savings without exceeding <see cref="AvailableToSave"/>.</summary>
    public Money MaxAdditionalSavings => MaxAdditionalSavingsAfter(Money.Zero(Currency));

    public Money MaxAdditionalSavingsAfter(Money priorSaved)
    {
        var headroom = AvailableToSaveAfter(priorSaved) - SavingsNetTotal;
        return headroom.IsNegative ? Money.Zero(Currency) : headroom;
    }

    /// <summary>The most that could be allocated to a single category's budget: the money in the account, minus what's
    /// budgeted in <i>other</i> categories and minus all savings (this period's plus <paramref name="priorSaved"/>).</summary>
    public Money MaxBudgetFor(Guid categoryId, Money priorSaved)
    {
        var othersBudgeted = BudgetedTotal - (FindBudget(categoryId)?.Allocated ?? Money.Zero(Currency));
        var headroom = ExpectedClosingBalance - othersBudgeted - SavingsNetTotal - priorSaved;
        return headroom.IsNegative ? Money.Zero(Currency) : headroom;
    }

    // --- Lifecycle --------------------------------------------------------

    public void Close() => Status = PeriodStatus.Closed;

    /// <summary>Re-open a previously closed period (used when the following period is removed).</summary>
    public void Reopen() => Status = PeriodStatus.Open;

    /// <summary>
    /// Physical money expected to carry into the next period: real opening balances + <b>new</b> deposits −
    /// expenses − money sent out to other accounts. The "From previous period" carryover is excluded — it lives
    /// in <see cref="CarriedIn"/>, not in <see cref="ContributionsPaidTotal"/>, since it's already represented in
    /// the real opening balances; counting it again would double the carried money.
    /// </summary>
    public Money ExpectedClosingBalance =>
        InitialTotal + ContributionsPaidTotal - ExpensesTotal - ExternalOutTotal;

    /// <summary>
    /// Savings earmarked beyond the cash actually left (i.e. expenses ate into the savings earmark).
    /// Zero when fully funded; positive means this much must be restored next period (from a savings
    /// bucket or fresh contributions) to start clean.
    /// </summary>
    public Money Deficit
    {
        get
        {
            var shortfall = SavingsNetTotal - ExpectedClosingBalance;
            return shortfall.IsNegative ? Money.Zero(Currency) : shortfall;
        }
    }

    // --- Helpers ----------------------------------------------------------

    private void EnsureOpen()
    {
        if (Status != PeriodStatus.Open)
            throw new InvalidOperationException("The period is closed.");
    }

    private void EnsureCurrency(Money money)
    {
        if (money.Currency != Currency)
            throw new InvalidOperationException($"Currency mismatch: period is {Currency}, value is {money.Currency}.");
    }

    private Money Sum(IEnumerable<Money> values) =>
        values.Aggregate(Money.Zero(Currency), (acc, m) => acc + m);
}
