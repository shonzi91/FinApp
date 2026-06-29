using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Contracts;
using FinApp.Persistence;
using Xunit;

namespace FinApp.Persistence.Tests;

public class SnapshotSerializerTests
{
    private static Money Eur(decimal v) => new(v, "EUR");

    private static Account BuildRichAccount(out Guid expenseFromSavingsId)
    {
        var owner = Guid.NewGuid();
        var account = new Account("Family", "EUR");
        account.AssignOwner(owner, "Owner");
        account.SetSavingsRateTarget(0.30m);
        var partner = account.AddContributor(Guid.NewGuid(), "Partner");

        account.AddDefaultFunds();
        var bank = account.FundId("Bank");
        var cash = account.FundId("Cash");

        var food = account.AddCategory("Food");
        account.AddCategory("Groceries", food.Id); // nested
        var fun = account.AddCategory("Fun");

        var vacations = account.AddSavingCategory("Vacations");
        account.ConfigureSavingGoal(vacations.Id, 2000m, 0.75m, notifyOnMilestone: true);

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.SetInitialBalance(bank, Eur(1000));
        period.SetInitialBalance(cash, Eur(200));
        period.Deposit(owner, Eur(600));
        period.AddBudget(food.Id, Eur(400), 0.9m, notifyOnEveryExpense: true);
        period.AddExpense(new Expense(food.Id, Eur(45), new DateOnly(2026, 1, 4), owner, bank, "Lunch"));
        period.TransferFunds(bank, cash, Eur(100), new DateOnly(2026, 1, 6), "top up wallet");
        period.AllocateToSavings(vacations.Id, Eur(150), new DateOnly(2026, 1, 7), "set aside");
        var fromSavings = period.ConvertSavingToExpense(vacations.Id, fun.Id, Eur(50), new DateOnly(2026, 1, 9), partner.UserId, cash, "day trip");
        expenseFromSavingsId = fromSavings.Id;

        return account;
    }

    [Fact]
    public void Round_trips_the_full_aggregate_preserving_ids_and_links()
    {
        var original = BuildRichAccount(out var savingsExpenseId);

        var json = AccountSnapshotSerializer.Serialize(original);
        var copy = AccountSnapshotSerializer.Deserialize(json);

        // Header
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Currency, copy.Currency);
        Assert.Equal(0.30m, copy.SavingsRateTarget);
        Assert.Equal(original.OwnerUserId, copy.OwnerUserId);
        Assert.True(copy.IsOwner(original.OwnerUserId));
        Assert.Equal(original.Members.Select(m => (m.UserId, m.DisplayName)),
                     copy.Members.Select(m => (m.UserId, m.DisplayName)));

        // Funds & categories (ids preserved so references resolve)
        Assert.Equal(original.Funds.Select(f => (f.Id, f.Name)), copy.Funds.Select(f => (f.Id, f.Name)));
        Assert.Equal(original.Categories.Select(c => (c.Id, c.Name, c.ParentId)),
                     copy.Categories.Select(c => (c.Id, c.Name, c.ParentId)));

        // Savings goal
        var savCopy = copy.SavingCategories.Single();
        Assert.Equal(2000m, savCopy.GoalAmount);
        Assert.Equal(0.75m, savCopy.AlertThreshold);
        Assert.True(savCopy.NotifyOnMilestone);

        // Period & computed values must match exactly (proves links survived)
        var op = original.Periods.Single();
        var cp = copy.Periods.Single();
        Assert.Equal(op.Id, cp.Id);
        Assert.Equal(op.ExpectedClosingBalance, cp.ExpectedClosingBalance);
        Assert.Equal(op.ExpensesTotal, cp.ExpensesTotal);
        Assert.Equal(op.SavingsNetTotal, cp.SavingsNetTotal);
        Assert.Equal(op.ContributionsPaidTotal, cp.ContributionsPaidTotal);
        Assert.Equal(op.FundBalance(original.FundId("Bank")), cp.FundBalance(copy.FundId("Bank")));

        // The savings-funded expense keeps its drawdown link (SourceExpenseId -> expense id)
        Assert.Contains(cp.SavingAllocations, a => a.SourceExpenseId == savingsExpenseId);
        Assert.Contains(cp.Expenses, e => e.Id == savingsExpenseId && e.SourceSavingCategoryId is not null);
    }

    [Fact]
    public void Closed_period_status_survives()
    {
        var account = new Account("Solo", "EUR");
        account.AssignOwner(Guid.NewGuid(), "Me");
        var p = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        p.Close();

        var copy = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Equal(PeriodStatus.Closed, copy.Periods.Single().Status);
    }

    [Fact]
    public void Legacy_snapshot_without_savings_target_defaults_to_20_percent()
    {
        // A snapshot produced before SavingsRateTarget existed has no such field.
        var legacy = """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"Old","Currency":"EUR",
             "OwnerUserId":"22222222-2222-2222-2222-222222222222","Members":[],"Funds":[],
             "Categories":[],"SavingCategories":[],"Periods":[]}
            """;

        var account = AccountSnapshotSerializer.Deserialize(legacy);
        Assert.Equal(0.20m, account.SavingsRateTarget);
    }
}
