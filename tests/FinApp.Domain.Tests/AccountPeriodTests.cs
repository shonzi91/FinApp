using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

public class AccountPeriodTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Starting_period_can_copy_budgets_forward()
    {
        var account = new Account("Family", Eur);
        var food = account.AddCategory("Food");
        var bills = account.AddCategory("Bills");

        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AddBudget(food.Id, M(400), alertThreshold: 0.75m, notifyOnEveryExpense: true);
        jan.AddBudget(bills.Id, M(250));

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), copyBudgetsFromPrevious: true);

        Assert.Equal(2, feb.Budgets.Count);
        var copiedFood = feb.FindBudget(food.Id);
        Assert.NotNull(copiedFood);
        Assert.Equal(M(400), copiedFood!.Allocated);
        Assert.Equal(0.75m, copiedFood.AlertThreshold);
        Assert.True(copiedFood.NotifyOnEveryExpense);
    }

    [Fact]
    public void Copying_budgets_forward_can_adjust_to_previous_consumption()
    {
        var account = new Account("Family", Eur);
        var food = account.AddCategory("Food");
        var bills = account.AddCategory("Bills");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();

        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AddBudget(food.Id, M(400));
        jan.AddBudget(bills.Id, M(250));
        jan.AddExpense(new Expense(food.Id, M(470), new DateOnly(2026, 1, 10), member, fund));  // overspent
        jan.AddExpense(new Expense(bills.Id, M(100), new DateOnly(2026, 1, 12), member, fund)); // underspent

        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28),
            copyBudgetsFromPrevious: true, adjustToConsumption: true);

        // Overspent: halfway from 400 to 470 is 435, rounded up to the next 10 -> 440.
        Assert.Equal(M(440), feb.FindBudget(food.Id)!.Allocated);
        // Underspent: halfway from 250 to 100 is 175, rounded up to the next 10 -> 180.
        Assert.Equal(M(180), feb.FindBudget(bills.Id)!.Allocated);
    }

    [Fact]
    public void Settling_an_expense_reduces_it_and_unsettling_restores_it()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var fund = Guid.NewGuid();
        var member = Guid.NewGuid();
        var destAccount = Guid.NewGuid();

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var expense = period.AddExpense(new Expense(food.Id, M(100), new DateOnly(2026, 1, 5), member, fund, onBehalfOfOtherAccount: true));

        var settlementId = Guid.NewGuid();
        var settled = period.SetSettlement(expense.Id, settlementId, destAccount, M(40));

        Assert.Equal(M(60), settled.Amount);                 // reduced by the settled amount
        Assert.Equal(40m, settled.SettledAmount);
        Assert.Equal(M(100), settled.OriginalAmount);
        Assert.True(settled.IsSettlementSource);
        Assert.Equal(destAccount, settled.SettledToAccountId);
        Assert.Equal(M(60), period.ExpensesTotal);           // only the un-settled portion is this account's cost

        // Re-settling recomputes from the original, not the already-reduced amount.
        var resettled = period.SetSettlement(settled.Id, settlementId, destAccount, M(70));
        Assert.Equal(M(30), resettled.Amount);

        // Unsettling (amount 0) restores the full amount and clears the link.
        var restored = period.SetSettlement(resettled.Id, settlementId, destAccount, M(0));
        Assert.Equal(M(100), restored.Amount);
        Assert.False(restored.IsSettlementSource);
        Assert.Null(restored.SettledToAccountId);
        Assert.Equal(M(100), period.ExpensesTotal);
    }

    [Fact]
    public void Duplicate_member_is_rejected()
    {
        var account = new Account("Shared", Eur);
        var userId = Guid.NewGuid();
        account.AddMember(userId, "Stoyan");

        Assert.Throws<InvalidOperationException>(() => account.AddMember(userId, "Stoyan again"));
    }

    [Fact]
    public void Deposits_accumulate_per_member()
    {
        var account = new Account("Shared", Eur);
        var a = account.AddMember(Guid.NewGuid(), "A");
        var b = account.AddMember(Guid.NewGuid(), "B");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(a.UserId, M(500));
        period.Deposit(a.UserId, M(100)); // a second deposit adds on
        period.Deposit(b.UserId, M(200));

        Assert.Equal(M(800), period.ContributionsPaidTotal);
        Assert.Equal(M(600), period.Contributions.First(c => c.MemberId == a.UserId).Paid);
    }

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively()
    {
        var account = new Account("Home", Eur);
        account.AddCategory("Food");
        Assert.Throws<InvalidOperationException>(() => account.AddCategory("food"));
        account.AddSavingCategory("Reserve");
        Assert.Throws<InvalidOperationException>(() => account.AddSavingCategory("RESERVE"));
        account.AddFund("Bank");
        Assert.Throws<InvalidOperationException>(() => account.AddFund(" bank "));

        var fun = account.AddCategory("Fun");
        Assert.Throws<InvalidOperationException>(() => account.RenameCategory(fun.Id, "Food")); // rename collision
    }

    [Fact]
    public void Expense_on_closed_period_is_rejected()
    {
        var account = new Account("Personal", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Close();

        Assert.Throws<InvalidOperationException>(() =>
            period.AddExpense(new Expense(food.Id, M(10), new DateOnly(2026, 1, 5), member.UserId, Guid.NewGuid())));
    }
}
