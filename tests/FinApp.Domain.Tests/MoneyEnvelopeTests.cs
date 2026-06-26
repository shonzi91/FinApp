using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class MoneyEnvelopeTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    private static Period PeriodWith(decimal opening, decimal contributed, out Account account, out Guid fund, out Guid category)
    {
        account = new Account("Home", Eur);
        account.AddDefaultFunds();
        account.AddCategory("Food");
        fund = account.FundId("Bank");
        category = account.Categories[0].Id;
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(fund, M(opening));
        if (contributed > 0)
        {
            var member = account.AddMember(Guid.NewGuid(), "A");
            period.Deposit(member.UserId, M(contributed), fundId: fund);
        }
        return period;
    }

    [Fact]
    public void Free_to_allocate_is_cash_minus_savings_ignoring_budgets()
    {
        // €1000 in, budget Food €600, already spent €550 of it, €100 saved.
        var period = PeriodWith(opening: 0, contributed: 1000, out _, out var fund, out var category);
        period.SetBudget(category, M(600));
        period.AddExpense(new Expense(category, M(550), new DateOnly(2026, 1, 5), Guid.NewGuid(), fund));
        period.AllocateToSavings(Guid.NewGuid(), M(100), new DateOnly(2026, 1, 6));

        // Closing is €450 (1000 − 550). Budgets don't reserve cash; spending is counted once (in closing).
        Assert.Equal(M(450), period.ExpectedClosingBalance);
        Assert.Equal(M(350), period.FreeToAllocateAfter(M(0)));   // 450 cash − 100 saved (budget ignored)
    }

    [Fact]
    public void Budget_is_capped_at_current_minus_savings_plus_spent_savings_stays_advisory()
    {
        // 2000 in, 1000 spent → current 1000; 500 saved → ceiling = current 1000 − saved 500 + spent 1000 = 1500.
        var period = PeriodWith(opening: 0, contributed: 2000, out _, out var fund, out var category);
        period.AllocateToSavings(Guid.NewGuid(), M(500), new DateOnly(2026, 1, 2));
        period.AddExpense(new Expense(category, M(1000), new DateOnly(2026, 1, 3), Guid.NewGuid(), fund));

        Assert.Equal(M(1000), period.ExpectedClosingBalance);       // current after spending
        Assert.Equal(M(1500), period.BudgetCeilingAfter(M(0)));     // current(1000) − saved(500) + spent(1000)
        period.SetBudget(category, M(1500));                         // up to the ceiling is allowed
        Assert.Equal(M(1500), period.BudgetedTotal);
        Assert.Throws<InvalidOperationException>(() => period.SetBudget(category, M(1501))); // past it is blocked

        // Savings is NOT capped — saving past the cash is still advisory (no throw).
        period.AllocateToSavings(Guid.NewGuid(), M(5000), new DateOnly(2026, 1, 4));
    }

    [Fact]
    public void Prior_period_savings_are_reserved_from_the_free_to_allocate_figure()
    {
        // 500 carried in (e.g. previously saved money sitting in the opening balance). With 200 already saved in
        // earlier periods, only 300 of it is free to budget, save again, or send to another account.
        var period = PeriodWith(opening: 500, contributed: 0, out _, out var fund, out var category);
        var priorSaved = M(200);

        Assert.Equal(M(300), period.MaxAdditionalSavingsAfter(priorSaved));        // 500 - 200
        Assert.Equal(M(300), period.AvailableToSaveAfter(priorSaved));
        Assert.Equal(M(300), period.FreeToAllocateAfter(priorSaved));
        Assert.Equal(M(300), period.AvailableToTransferOutFromFundAfter(fund, priorSaved));

        // Prior savings stay reserved: saving the un-reserved 300 this period leaves nothing free, and saving
        // past it drives "free to allocate" negative (allowed, not blocked). Budgets don't enter the figure.
        _ = category;
        period.AllocateToSavings(Guid.NewGuid(), M(300), new DateOnly(2026, 1, 5));
        Assert.Equal(M(0), period.FreeToAllocateAfter(priorSaved));
        period.AllocateToSavings(Guid.NewGuid(), M(50), new DateOnly(2026, 1, 6));
        Assert.True(period.FreeToAllocateAfter(priorSaved).IsNegative);   // 500 - 350 saved - 200 prior
    }

    [Fact]
    public void Opening_funds_count_toward_the_savings_ceiling()
    {
        // You can set aside money you actually have — an opening balance (e.g. carried over) is savable.
        var period = PeriodWith(opening: 500, contributed: 0, out _, out _, out _);
        Assert.Equal(M(500), period.MaxAdditionalSavings);          // the full opening balance is available
        period.AllocateToSavings(Guid.NewGuid(), M(500), new DateOnly(2026, 1, 2));
        Assert.Equal(M(0), period.MaxAdditionalSavings);
        // Saving beyond the balance is advisory now: it succeeds and free-to-allocate goes negative.
        period.AllocateToSavings(Guid.NewGuid(), M(1), new DateOnly(2026, 1, 3));
        Assert.True(period.FreeToAllocateAfter(M(0)).IsNegative);
    }

    [Fact]
    public void Savings_can_be_moved_between_buckets_without_changing_the_total()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var vacations = account.AddSavingCategory("Vacations");
        var car = account.AddSavingCategory("Car");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000));
        period.AllocateToSavings(vacations.Id, M(300), new DateOnly(2026, 1, 2));

        period.TransferSavings(vacations.Id, car.Id, M(120), new DateOnly(2026, 1, 3));

        Assert.Equal(M(300), period.SavingsNetTotal); // total unchanged
        Assert.Equal(M(180), account.SavingCategoryWithDescendantIds(vacations.Id)
            .Select(id => period.SavingAllocations.Where(a => a.SavingCategoryId == id).Aggregate(M(0), (s, a) => s + a.Amount))
            .Aggregate(M(0), (s, m) => s + m));
        Assert.Throws<ArgumentException>(() => period.TransferSavings(car.Id, car.Id, M(10), new DateOnly(2026, 1, 4)));
    }

    [Fact]
    public void Expenses_may_overspend_and_surface_a_deficit()
    {
        var period = PeriodWith(opening: 0, contributed: 1000, out _, out var fund, out var category);
        period.SetBudget(category, M(500));
        period.AllocateToSavings(Guid.NewGuid(), M(500), new DateOnly(2026, 1, 2));

        // Spend 510 against the 500 budget — allowed, but it eats 10 into the savings earmark.
        period.AddExpense(new Expense(category, M(510), new DateOnly(2026, 1, 5), Guid.NewGuid(), fund));

        Assert.Equal(M(490), period.ExpectedClosingBalance); // 1000 - 510
        Assert.Equal(M(10), period.Deficit);                 // 500 saved - 490 cash left
    }

    [Fact]
    public void Bucket_initial_amount_counts_toward_balance_but_not_the_savings_rate()
    {
        // The reported bug: 3000 contributed + a 10000 pre-existing savings balance must not inflate the rate.
        var account = new Account("Home", Eur);
        var bucket = account.AddSavingCategory("Reserve");
        account.SetSavingInitialAmount(bucket.Id, 10000m);
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(3000));
        period.AllocateToSavings(bucket.Id, M(600), new DateOnly(2026, 1, 5));

        var report = new SavingsReportService();
        Assert.Equal(M(10600), report.ForBucket(account, period, bucket.Id).AccumulatedTotal); // 10000 + 600
        Assert.Equal(M(10600), report.AccumulatedTotal(account));
        Assert.Equal(0.2m, report.PeriodSavingsRate(period));   // 600 / 3000, initial excluded
        Assert.Equal(0.2m, report.AccountSavingsRate(account)); // 600 / 3000, initial excluded
    }

    [Fact]
    public void Opening_balances_carry_over_and_are_fully_allocatable()
    {
        // Carried money simply sits in the opening fund balances; it's all spendable/savable, no separate carryover.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var food = account.AddCategory("Food");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        period.SetInitialBalance(account.FundId("Bank"), M(1150)); // real opening (carried money sits here)
        period.Deposit(member.UserId, M(200));                      // new money this period

        Assert.Equal(M(200), period.ContributionsPaidTotal);        // new deposits only
        Assert.Equal(M(1350), period.ExpectedClosingBalance);       // 1150 opening + 200 new
        Assert.Equal(M(1350), period.AvailableToSave);              // nothing saved yet

        period.AllocateToSavings(account.AddSavingCategory("Reserve").Id, M(800), new DateOnly(2026, 2, 5));
        Assert.Equal(M(550), period.MaxAdditionalSavings);          // 1350 - 800 saved (budgets don't reduce it)
        _ = food;
    }

    [Fact]
    public void Transfer_out_to_another_account_reduces_the_fund_and_closing_balance()
    {
        var period = PeriodWith(opening: 1000, contributed: 0, out _, out var fund, out _);
        var transfer = period.TransferOut(fund, M(300), new DateOnly(2026, 1, 5), Guid.NewGuid(), "to Shared");

        Assert.Equal(M(300), period.ExternalOutTotal);
        Assert.Equal(M(700), period.FundBalance(fund));            // 1000 - 300 sent out
        Assert.Equal(M(700), period.ExpectedClosingBalance);       // outflow leaves the account

        period.RemoveExternalTransfer(transfer.Id);
        Assert.Equal(M(0), period.ExternalOutTotal);
        Assert.Equal(M(1000), period.ExpectedClosingBalance);
    }

    [Fact]
    public void Transfer_out_cannot_exceed_the_source_fund_balance()
    {
        // Bank holds 200; Cash holds 500. You can't send 300 out of Bank even though the account holds 700.
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var cash = account.FundId("Cash");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(bank, M(200));
        period.SetInitialBalance(cash, M(500));

        Assert.Equal(M(200), period.AvailableToTransferOutFromFund(bank));
        Assert.Throws<InvalidOperationException>(
            () => period.TransferOut(bank, M(300), new DateOnly(2026, 1, 5), Guid.NewGuid()));

        // Top Bank up from Cash first, then the 300 send works.
        period.TransferFunds(cash, bank, M(100), new DateOnly(2026, 1, 5));
        Assert.Equal(M(300), period.AvailableToTransferOutFromFund(bank));
        period.TransferOut(bank, M(300), new DateOnly(2026, 1, 6), Guid.NewGuid());
        Assert.Equal(M(0), period.FundBalance(bank));
    }

    [Fact]
    public void Internal_transfer_can_overdraw_a_fund_total_is_preserved()
    {
        var account = new Account("Home", Eur);
        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var cash = account.FundId("Cash");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(bank, M(100));

        // Moving more than Bank holds is allowed — it's just where the money sits. Bank goes negative, total unchanged.
        period.TransferFunds(bank, cash, M(150), new DateOnly(2026, 1, 5));
        Assert.Equal(M(-50), period.FundBalance(bank));
        Assert.Equal(M(150), period.FundBalance(cash));
        Assert.Equal(M(100), period.ExpectedClosingBalance); // total preserved (transfers don't change the closing balance)
    }

    [Fact]
    public void Transfer_out_dipping_into_savings_is_allowed_up_to_the_fund_balance()
    {
        // 1000 cash, 800 earmarked for savings → only 200 is "unreserved" (advisory).
        var period = PeriodWith(opening: 0, contributed: 1000, out _, out var fund, out _);
        period.AllocateToSavings(Guid.NewGuid(), M(800), new DateOnly(2026, 1, 2));

        Assert.Equal(M(200), period.AvailableToTransferOut);      // advisory threshold the UI warns past

        // Sending 300 dips into the savings earmark — now allowed (the UI warns), reducing the closing balance.
        period.TransferOut(fund, M(300), new DateOnly(2026, 1, 5), Guid.NewGuid());
        Assert.Equal(M(700), period.ExpectedClosingBalance);

        // But the fund's physical balance is still a hard limit — can't send the 800 that isn't there.
        Assert.Throws<InvalidOperationException>(
            () => period.TransferOut(fund, M(800), new DateOnly(2026, 1, 6), Guid.NewGuid()));
    }

    [Fact]
    public void Saving_moved_to_a_budget_is_listed_and_can_be_undone()
    {
        var period = PeriodWith(opening: 0, contributed: 1000, out _, out _, out var category);
        var bucket = Guid.NewGuid();
        period.AllocateToSavings(bucket, M(500), new DateOnly(2026, 1, 2));

        period.ConvertSavingToBudget(bucket, category, M(200), new DateOnly(2026, 1, 3));
        var move = Assert.Single(period.SavingMovements());
        Assert.Equal(M(200), period.FindBudget(category)!.Allocated);
        Assert.Equal(M(300), period.SavingsNetTotal);              // 500 set aside - 200 matured

        period.RemoveSavingMovement(move.Id);
        Assert.Empty(period.SavingMovements());
        Assert.Equal(M(500), period.SavingsNetTotal);              // earmark restored
        Assert.Equal(M(0), period.FindBudget(category)!.Allocated); // budget bump reversed
    }

    [Fact]
    public void Saving_moved_between_buckets_is_listed_once_and_removed_as_a_pair()
    {
        var account = new Account("Home", Eur);
        var vacations = account.AddSavingCategory("Vacations");
        var car = account.AddSavingCategory("Car");
        var member = account.AddMember(Guid.NewGuid(), "A");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(1000));
        period.AllocateToSavings(vacations.Id, M(300), new DateOnly(2026, 1, 2));

        period.TransferSavings(vacations.Id, car.Id, M(120), new DateOnly(2026, 1, 3));
        var move = Assert.Single(period.SavingMovements());        // only the outgoing half is listed
        Assert.Equal(2, period.SavingAllocations.Count(a => a.TransferPairId == move.TransferPairId));

        period.RemoveSavingMovement(move.Id);
        Assert.Empty(period.SavingMovements());
        Assert.Equal(M(300), period.SavingsNetTotal);              // net unchanged before and after
        Assert.DoesNotContain(period.SavingAllocations, a => a.TransferPairId is not null);
    }

    [Fact]
    public void Editing_a_deposit_overwrites_the_total_and_deleting_clears_it()
    {
        var period = PeriodWith(opening: 0, contributed: 100, out var account, out _, out _);
        var member = account.Members[0].UserId;
        Assert.Equal(M(100), period.ContributionsPaidTotal);

        var contrib = period.Contributions.First(c => c.MemberId == member);
        period.EditContribution(contrib.Id, M(250), contrib.CategoryId, contrib.FundId, contrib.Date);
        Assert.Equal(M(250), period.ContributionsPaidTotal);

        period.RemoveContribution(contrib.Id);
        Assert.Equal(M(0), period.ContributionsPaidTotal);
    }
}
