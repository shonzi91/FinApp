using FinApp.Domain.Accounts;
using Xunit;

namespace FinApp.Domain.Tests;

public class SavingsTargetTests
{
    [Fact]
    public void Defaults_to_20_percent()
    {
        var account = new Account("Solo", "EUR");
        Assert.Equal(0.20m, account.SavingsRateTarget);
    }

    [Fact]
    public void Can_be_set_to_a_valid_fraction()
    {
        var account = new Account("Solo", "EUR");
        account.SetSavingsRateTarget(0.35m);
        Assert.Equal(0.35m, account.SavingsRateTarget);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Rejects_a_fraction_outside_0_to_1(decimal target)
    {
        var account = new Account("Solo", "EUR");
        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetSavingsRateTarget(target));
    }
}
