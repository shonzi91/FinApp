using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Funds;
using FinApp.Domain.Periods;
using FinApp.Domain.Savings;
using FinApp.Domain.Sharing;
using FinApp.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinApp.Persistence;

/// <summary>
/// EF Core mapping for the FinApp aggregate. Maps directly onto the rich domain types:
/// collections use their private backing fields, <see cref="Money"/> is value-converted to text,
/// and entities are materialised through their (single) constructors.
///
/// Every scalar property is mapped explicitly. The domain entities are immutable (constructor-set,
/// get-only properties), so we don't rely on property-discovery conventions — explicit mapping
/// guarantees each constructor parameter has a property to bind to.
/// </summary>
public sealed class FinAppDbContext(DbContextOptions<FinAppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AccountSnapshotRow> AccountSnapshots => Set<AccountSnapshotRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var money = new MoneyConverter();

        b.Entity<Account>(a =>
        {
            a.ToTable("Accounts");
            Key(a);
            a.Property(x => x.Name).IsRequired();
            a.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            a.Property(x => x.OwnerUserId);

            a.Ignore(x => x.CurrentPeriod);
            a.Ignore(x => x.RootCategories);
            a.Ignore(x => x.RootSavingCategories);
            a.Ignore(x => x.RootFunds);
            // Savings target is body data — it rides in the account snapshot, not the relational header.
            // Ignoring it keeps the Accounts table unchanged (prod Postgres uses EnsureCreated; no migration).
            a.Ignore(x => x.SavingsRateTarget);

            OwnedList(a, x => x.Members);
            OwnedList(a, x => x.Categories);
            OwnedList(a, x => x.SavingCategories);
            OwnedList(a, x => x.ContributionCategories);
            OwnedList(a, x => x.Funds);
            OwnedList(a, x => x.Periods);
        });

        b.Entity<ContributionCategory>(c =>
        {
            c.ToTable("ContributionCategories");
            Key(c);
            c.Property(x => x.Name).IsRequired();
        });

        b.Entity<Fund>(f =>
        {
            f.ToTable("Funds");
            Key(f);
            f.Property(x => x.Name).IsRequired();
            f.Property(x => x.ParentId);
            f.Property(x => x.Note);
            f.Ignore(x => x.IsRoot);
        });

        b.Entity<User>(u =>
        {
            u.ToTable("Users");
            Key(u);
            u.Property(x => x.Username).IsRequired();
            u.Property(x => x.Email).IsRequired();
            u.Property(x => x.PasswordHash).IsRequired();
            u.HasIndex(x => x.Username).IsUnique();
            u.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Invitation>(i =>
        {
            i.ToTable("Invitations");
            Key(i);
            i.Property(x => x.AccountId);
            i.Property(x => x.InvitedUserId);
            i.Property(x => x.InvitedByUserId);
            i.Property(x => x.Status);
            i.Property(x => x.CreatedAt);
            i.Property(x => x.RespondedAt);
            i.HasIndex(x => x.InvitedUserId);
            i.HasIndex(x => new { x.AccountId, x.InvitedUserId });
        });

        b.Entity<AccountSnapshotRow>(s =>
        {
            s.ToTable("AccountSnapshots");
            s.HasKey(x => x.AccountId);
            s.Property(x => x.AccountId).ValueGeneratedNever();
            s.Property(x => x.Payload).IsRequired();
            s.Property(x => x.Version);
            s.Property(x => x.UpdatedAt);
        });

        b.Entity<AccountMember>(m =>
        {
            m.ToTable("Members");
            Key(m);
            m.Property(x => x.UserId);
            m.Property(x => x.DisplayName).IsRequired();
        });

        b.Entity<Category>(c =>
        {
            c.ToTable("Categories");
            Key(c);
            c.Property(x => x.Name).IsRequired();
            c.Property(x => x.ParentId);
            c.Ignore(x => x.IsRoot);
            // Icon is body data — rides in the snapshot, not the relational header (no migration; prod uses EnsureCreated).
            c.Ignore(x => x.Icon);
        });

        b.Entity<SavingCategory>(c =>
        {
            c.ToTable("SavingCategories");
            Key(c);
            c.Property(x => x.Name).IsRequired();
            c.Property(x => x.ParentId);
            c.Property(x => x.GoalAmount);
            c.Property(x => x.AlertThreshold);
            c.Property(x => x.NotifyOnMilestone);
            c.Property(x => x.InitialAmount);
            c.Ignore(x => x.IsRoot);
            c.Ignore(x => x.HasGoal);
        });

        b.Entity<Period>(p =>
        {
            p.ToTable("Periods");
            Key(p);
            p.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            p.Property(x => x.From);
            p.Property(x => x.To);
            p.Property(x => x.Status);
            p.Property(x => x.CarriedIn).HasConversion(money).IsRequired();

            p.Ignore(x => x.InitialTotal);
            p.Ignore(x => x.ContributionsPaidTotal);
            p.Ignore(x => x.ExpensesTotal);
            p.Ignore(x => x.SavingsNetTotal);
            p.Ignore(x => x.ExpectedClosingBalance);
            p.Ignore(x => x.BudgetedTotal);
            p.Ignore(x => x.AvailableToSave);
            p.Ignore(x => x.MaxAdditionalSavings);
            p.Ignore(x => x.LengthInDays);

            p.Ignore(x => x.ExternalOutTotal);

            OwnedList(p, x => x.InitialBalances);
            OwnedList(p, x => x.Contributions);
            OwnedList(p, x => x.Budgets);
            OwnedList(p, x => x.Expenses);
            OwnedList(p, x => x.SavingAllocations);
            OwnedList(p, x => x.FundTransfers);
            OwnedList(p, x => x.ExternalTransfers);
        });

        b.Entity<ExternalTransfer>(t =>
        {
            t.ToTable("ExternalTransfers");
            Key(t);
            t.Property(x => x.FundId);
            t.Property(x => x.Amount).HasConversion(money).IsRequired();
            t.Property(x => x.Date);
            t.Property(x => x.ToAccountId);
            t.Property(x => x.Note);
        });

        b.Entity<InitialBalance>(i =>
        {
            i.ToTable("InitialBalances");
            Key(i);
            i.Property(x => x.FundId);
            i.Property(x => x.Amount).HasConversion(money).IsRequired();
            i.Property(x => x.Informative);
        });

        b.Entity<FundTransfer>(t =>
        {
            t.ToTable("FundTransfers");
            Key(t);
            t.Property(x => x.FromFundId);
            t.Property(x => x.ToFundId);
            t.Property(x => x.Amount).HasConversion(money).IsRequired();
            t.Property(x => x.Date);
            t.Property(x => x.Note);
        });

        b.Entity<Contribution>(c =>
        {
            c.ToTable("Contributions");
            Key(c);
            c.Property(x => x.MemberId);
            c.Property(x => x.CategoryId);
            c.Property(x => x.FundId);
            c.Property(x => x.Date);
            c.Property(x => x.Paid).HasConversion(money).IsRequired();
        });

        b.Entity<Budget>(bu =>
        {
            bu.ToTable("Budgets");
            Key(bu);
            bu.Property(x => x.CategoryId);
            bu.Property(x => x.Allocated).HasConversion(money).IsRequired();
            bu.Property(x => x.AlertThreshold);
            bu.Property(x => x.NotifyOnEveryExpense);
        });

        b.Entity<Expense>(e =>
        {
            e.ToTable("Expenses");
            Key(e);
            e.Property(x => x.CategoryId);
            e.Property(x => x.Amount).HasConversion(money).IsRequired();
            e.Property(x => x.Date);
            e.Property(x => x.MemberId);
            e.Property(x => x.FundId);
            e.Property(x => x.Note);
            e.Property(x => x.SourceSavingCategoryId);
            e.Property(x => x.OnBehalfOfOtherAccount);
            e.Property(x => x.SettlementId);
            e.Property(x => x.SettledToAccountId);
            e.Property(x => x.SettledFromAccountId);
            e.Property(x => x.SettledAmount);
            e.Ignore(x => x.IsFromSavings);
            e.Ignore(x => x.SettledMoney);
            e.Ignore(x => x.IsSettlementSource);
            e.Ignore(x => x.IsSettlementDestination);
            e.Ignore(x => x.OriginalAmount);
        });

        b.Entity<SavingAllocation>(s =>
        {
            s.ToTable("SavingAllocations");
            Key(s);
            s.Property(x => x.SavingCategoryId);
            s.Property(x => x.Amount).HasConversion(money).IsRequired();
            s.Property(x => x.Date);
            s.Property(x => x.Note);
            s.Property(x => x.SourceExpenseId);
            s.Property(x => x.BudgetCategoryId);
            s.Property(x => x.TransferPairId);
        });
    }

    private static void Key<T>(EntityTypeBuilder<T> b) where T : Domain.Common.Entity
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
    }

    /// <summary>Configure a parent-owned one-to-many backed by the parent's private field (cascade delete).</summary>
    private static void OwnedList<TParent, TChild>(
        EntityTypeBuilder<TParent> parent,
        System.Linq.Expressions.Expression<Func<TParent, IEnumerable<TChild>?>> navigation)
        where TParent : class
        where TChild : class
    {
        parent.HasMany(navigation).WithOne().OnDelete(DeleteBehavior.Cascade);
        parent.Navigation(navigation).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
