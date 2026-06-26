using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
using FinApp.Domain.Services;
using Xunit;

namespace FinApp.Domain.Tests;

public class ReallocationAndCapTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    private static (Account account, Period period, Category food, SavingCategory vac) Setup(
        decimal contributed, decimal foodBudget, decimal foodSpent)
    {
        var account = new Account("Family", Eur);
        var member = account.AddMember(Guid.NewGuid(), "Stoyan");
        var food = account.AddCategory("Food");
        var vac = account.AddSavingCategory("Vacations");

        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.Deposit(member.UserId, M(contributed));
        period.AddBudget(food.Id, M(foodBudget));
        if (foodSpent > 0)
            period.AddExpense(new Expense(food.Id, M(foodSpent), new DateOnly(2026, 1, 5), member.UserId, Guid.NewGuid()));
        return (account, period, food, vac);
    }

    [Fact]
    public void Saving_past_the_unallocated_cash_is_advisory_not_blocked()
    {
        var (_, period, _, vac) = Setup(contributed: 1000, foodBudget: 800, foodSpent: 0);

        Assert.Equal(M(200), period.MaxAdditionalSavings);            // 1000 - 800
        period.AllocateToSavings(vac.Id, M(200), new DateOnly(2026, 1, 6)); // uses up the headroom
        Assert.Equal(M(0), period.FreeToAllocateAfter(M(0)));

        // Saving one more euro no longer throws — it's allowed and shows up as negative free-to-allocate.
        period.AllocateToSavings(vac.Id, M(1), new DateOnly(2026, 1, 7));
        Assert.True(period.FreeToAllocateAfter(M(0)).IsNegative);
    }

    [Fact]
    public void Reallocate_budget_leftover_to_savings()
    {
        var (account, period, _, vac) = Setup(contributed: 1000, foodBudget: 600, foodSpent: 400);
        // Leftover on food = 600 - 400 = 200. Budgeted=600 so only 400 was savable; move leftover into savings.
        new BudgetReallocationService().ToSavings(account, period, account.Categories[0].Id, vac.Id, M(150), new DateOnly(2026, 1, 10));

        Assert.Equal(M(450), period.FindBudget(account.Categories[0].Id)!.Allocated); // 600 - 150
        Assert.Equal(M(150), period.SavingsNetTotal);
    }

    [Fact]
    public void Cannot_reallocate_more_than_budget_leftover()
    {
        var (account, period, food, vac) = Setup(contributed: 1000, foodBudget: 600, foodSpent: 400);
        var svc = new BudgetReallocationService();

        Assert.Equal(M(200), svc.Leftover(account, period, food.Id));
        Assert.Throws<InvalidOperationException>(
            () => svc.ToSavings(account, period, food.Id, vac.Id, M(250), new DateOnly(2026, 1, 10)));
    }

    [Fact]
    public void Reallocate_between_budgets_moves_allocation()
    {
        var (account, period, food, _) = Setup(contributed: 1000, foodBudget: 600, foodSpent: 100);
        var fun = account.AddCategory("Fun");
        period.AddBudget(fun.Id, M(100));

        new BudgetReallocationService().ToBudget(account, period, food.Id, fun.Id, M(200));

        Assert.Equal(M(400), period.FindBudget(food.Id)!.Allocated); // 600 - 200
        Assert.Equal(M(300), period.FindBudget(fun.Id)!.Allocated);  // 100 + 200
    }

    [Fact]
    public void Rescheduling_a_period_shifts_later_periods_contiguously()
    {
        var account = new Account("Family", Eur);
        var jan = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        var feb = account.StartPeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)); // 27-day length

        // Move January to start mid-month; February should follow on the next day, keeping its length.
        account.ReschedulePeriod(jan, new DateOnly(2026, 1, 15), new DateOnly(2026, 2, 14));

        Assert.Equal(new DateOnly(2026, 2, 15), feb.From);
        Assert.Equal(feb.From.AddDays(27), feb.To);
    }
}
