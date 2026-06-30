using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using Xunit;

namespace FinApp.Domain.Tests;

public class CategoryAdminTests
{
    private const string Eur = "EUR";
    private static Money M(decimal v) => new(v, Eur);

    [Fact]
    public void Category_icon_defaults_to_null_and_can_be_set_and_cleared()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food", icon: "🍽️");
        Assert.Equal("🍽️", food.Icon);

        var other = account.AddCategory("Other");
        Assert.Null(other.Icon);

        account.SetCategoryIcon(other.Id, "🎁");
        Assert.Equal("🎁", other.Icon);

        account.SetCategoryIcon(other.Id, "  ");   // blank clears it
        Assert.Null(other.Icon);
    }

    [Fact]
    public void Category_with_a_budget_cannot_be_removed()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food.Id, M(100));

        Assert.Equal("a budget references it", account.CategoryRemovalBlocker(food.Id));
        Assert.Throws<InvalidOperationException>(() => account.RemoveCategory(food.Id));
    }

    [Fact]
    public void Category_with_children_cannot_be_removed()
    {
        var account = new Account("Personal", Eur);
        var kids = account.AddCategory("Kids");
        account.AddCategory("Kid1", kids.Id);

        Assert.Equal("it has sub-categories", account.CategoryRemovalBlocker(kids.Id));
    }

    [Fact]
    public void Unused_category_is_removed()
    {
        var account = new Account("Personal", Eur);
        var spare = account.AddCategory("Spare");
        account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Null(account.CategoryRemovalBlocker(spare.Id));
        account.RemoveCategory(spare.Id);
        Assert.Empty(account.Categories);
    }

    [Fact]
    public void Budget_can_be_removed_from_a_period()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var period = account.StartPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AddBudget(food.Id, M(100));

        period.RemoveBudget(food.Id);
        Assert.Null(period.FindBudget(food.Id));
    }
}
