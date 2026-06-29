using System.Globalization;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;
using FinApp.Domain.Services;

namespace FinApp.Shared.UI.Services;

/// <summary>How a delta should read: <c>Up</c> = spending/cost rose (bad, red), <c>Down</c> = fell (good, green), <c>Flat</c> = neutral.</summary>
public enum DeltaDir { Up, Down, Flat }

/// <summary>Health band the score falls into; drives gauge colour + verdict copy.</summary>
public enum HealthBand { AtRisk, Average, Healthy }

/// <summary>Kind of signal card (matches the template's warn / good / info icon tints).</summary>
public enum SignalKind { Warn, Good, Info }

public sealed record Signal(SignalKind Kind, string Title, string Desc, string Delta, DeltaDir Dir);

public sealed record CategorySpend(string Name, Money Amount, decimal BarFraction, DeltaDir Dir, string ColorHex);

public sealed record TrendPoint(string Label, Money Outgoings, decimal BarFraction, bool IsCurrent);

public sealed record QuickWin(string Text);

/// <summary>
/// A read-only financial-health/insights view model for one period of an account, derived entirely from
/// existing domain reads (no stored state). Built by <see cref="InsightsService"/>.
/// </summary>
public sealed record FinancialHealthReport(
    bool HasData,
    string PeriodLabel,
    int Score,
    int? ScoreDelta,
    HealthBand Band,
    string Verdict,
    string Summary,
    decimal? SavingsRate,
    decimal SavingsTarget,
    Money? SavingsShortfall,
    string SavingsCritique,
    bool TrendUp,
    string TrendNote,
    IReadOnlyList<Signal> Signals,
    IReadOnlyList<CategorySpend> Breakdown,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<QuickWin> QuickWins);

/// <summary>
/// Computes the Insights tab's financial-health report for a given period. Pure presentation-layer logic over
/// the domain aggregate's public reads — it adds no domain concepts and stores nothing. The savings-rate target
/// is a fixed default (<see cref="DefaultSavingsTarget"/>) until/unless it becomes a per-account setting.
/// </summary>
public sealed class InsightsService
{
    public const decimal DefaultSavingsTarget = 0.20m;

    // A harmonious mint-family palette for the spending breakdown (cycled).
    private static readonly string[] Palette =
        ["#13a06e", "#0e7c55", "#f5a623", "#ff7a59", "#4da6ff", "#a78bfa", "#e0608a", "#36c5c0"];

    private readonly SavingsReportService _savings = new();

    public FinancialHealthReport Build(Account account, int periodIndex, Func<Money, string> fmt)
    {
        ArgumentNullException.ThrowIfNull(account);
        var periods = account.Periods;
        var currency = account.Currency;

        if (periodIndex < 0 || periodIndex >= periods.Count)
            return Empty(currency);

        var p = periods[periodIndex];
        var label = p.From.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        var target = account.SavingsRateTarget;

        var income = p.ContributionsPaidTotal;
        var spend = p.ExpensesTotal;
        var hasData = spend.Amount > 0 || income.Amount > 0 || p.BudgetedTotal.Amount > 0;
        if (!hasData)
            return Empty(currency) with { PeriodLabel = label, SavingsTarget = target };

        var savingsRate = _savings.PeriodSavingsRate(p);

        var breakdown = BuildBreakdown(account, periods, periodIndex);
        var trend = BuildTrend(periods, periodIndex, currency);

        var score = ComputeScore(account, periodIndex, target);
        int? delta = periodIndex > 0 ? score - ComputeScore(account, periodIndex - 1, target) : null;
        var band = BandFor(score);

        var (verdict, summary) = Narrative(band, delta);

        // Savings shortfall vs target (per period), only when below target and there's income to measure against.
        Money? shortfall = null;
        if (income.Amount > 0 && (savingsRate ?? 0m) < target)
        {
            var gap = (target - (savingsRate ?? 0m)) * income.Amount;
            if (gap > 0m) shortfall = new Money(decimal.Round(gap, 2), currency);
        }
        var savingsCritique = SavingsCritique(savingsRate, shortfall, target, fmt);

        var (trendUp, trendNote) = TrendNarrative(trend, fmt);

        var signals = BuildSignals(account, periods, periodIndex, savingsRate, target, fmt);
        var wins = BuildQuickWins(account, p, savingsRate, income, target, fmt);

        return new FinancialHealthReport(
            HasData: true,
            PeriodLabel: label,
            Score: score,
            ScoreDelta: delta,
            Band: band,
            Verdict: verdict,
            Summary: summary,
            SavingsRate: savingsRate,
            SavingsTarget: target,
            SavingsShortfall: shortfall,
            SavingsCritique: savingsCritique,
            TrendUp: trendUp,
            TrendNote: trendNote,
            Signals: signals,
            Breakdown: breakdown,
            Trend: trend,
            QuickWins: wins);
    }

    private static FinancialHealthReport Empty(string currency) => new(
        HasData: false, PeriodLabel: "", Score: 0, ScoreDelta: null, Band: HealthBand.Average,
        Verdict: "", Summary: "", SavingsRate: null, SavingsTarget: DefaultSavingsTarget,
        SavingsShortfall: null, SavingsCritique: "", TrendUp: false, TrendNote: "",
        Signals: [], Breakdown: [], Trend: [], QuickWins: []);

    // --- Score (0..100, four equally-weighted 25-pt components) ----------------------------------

    private int ComputeScore(Account account, int idx, decimal target)
    {
        var p = account.Periods[idx];

        // 1) Savings vs target.
        var rate = _savings.PeriodSavingsRate(p) ?? 0m;
        if (rate < 0m) rate = 0m;
        var sav = Math.Min(1m, target <= 0m ? 1m : rate / target) * 25m;

        // 2) Budget adherence (neutral when nothing is budgeted — can't assess).
        decimal adh;
        if (p.BudgetedTotal.Amount > 0m)
        {
            var over = OverspendTotal(account, p).Amount;
            var frac = Math.Clamp(over / p.BudgetedTotal.Amount, 0m, 1m);
            adh = (1m - frac) * 25m;
        }
        else adh = 15m;

        // 3) Living within means (no deficit, non-negative closing).
        decimal wm;
        if (p.Deficit.Amount <= 0m && p.ExpectedClosingBalance.Amount >= 0m)
            wm = 25m;
        else
        {
            var baseAmt = p.ContributionsPaidTotal.Amount > 0m
                ? p.ContributionsPaidTotal.Amount
                : Math.Max(1m, p.ExpensesTotal.Amount);
            var d = Math.Clamp(p.Deficit.Amount / baseAmt, 0m, 1m);
            wm = (1m - d) * 25m;
        }

        // 4) Spending trend vs trailing average (neutral when no history).
        decimal tr;
        var priorAvg = TrailingAverageOutgoings(account.Periods, idx, 3);
        if (priorAvg is not { } avg) tr = 15m;
        else
        {
            var cur = p.ExpensesTotal.Amount;
            var ratio = avg <= 0m ? (cur > 0m ? 1.5m : 0m) : cur / avg;
            tr = Math.Clamp(1.5m - ratio, 0m, 1m) * 25m;
        }

        return (int)Math.Round(sav + adh + wm + tr, MidpointRounding.AwayFromZero);
    }

    private static decimal? TrailingAverageOutgoings(IReadOnlyList<Period> periods, int idx, int count)
    {
        var start = Math.Max(0, idx - count);
        if (start >= idx) return null;
        decimal sum = 0m;
        var n = 0;
        for (var i = start; i < idx; i++) { sum += periods[i].ExpensesTotal.Amount; n++; }
        return n == 0 ? null : sum / n;
    }

    private Money OverspendTotal(Account account, Period p)
    {
        var total = Money.Zero(account.Currency);
        foreach (var b in p.Budgets)
        {
            var spent = SpentInTree(account, p, b.CategoryId);
            if (spent > b.Allocated) total += spent - b.Allocated;
        }
        return total;
    }

    private static Money SpentInTree(Account account, Period p, Guid rootCategoryId)
    {
        var ids = account.CategoryWithDescendantIds(rootCategoryId).ToHashSet();
        return p.Expenses.Where(e => ids.Contains(e.CategoryId))
            .Select(e => e.Amount)
            .Aggregate(Money.Zero(p.Currency), (acc, m) => acc + m);
    }

    private static HealthBand BandFor(int score) =>
        score >= 70 ? HealthBand.Healthy : score >= 40 ? HealthBand.Average : HealthBand.AtRisk;

    private static (string Verdict, string Summary) Narrative(HealthBand band, int? delta)
    {
        var move = delta switch
        {
            > 0 => $" You're up {delta} points from last month.",
            < 0 => $" You're down {-delta} points from last month.",
            _ => ""
        };
        return band switch
        {
            HealthBand.Healthy => ("Looking healthy",
                "Your habits are solid — saving steadily, spending within plan." + move),
            HealthBand.Average => ("Getting there",
                "Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different." + move),
            _ => ("Needs attention",
                "A few things need a look this period — overspending or thin savings. Small fixes add up fast." + move),
        };
    }

    // --- Spending breakdown by root category ----------------------------------------------------

    private static IReadOnlyList<CategorySpend> BuildBreakdown(Account account, IReadOnlyList<Period> periods, int idx)
    {
        var p = periods[idx];
        var prev = idx > 0 ? periods[idx - 1] : null;

        var rows = new List<(Category Cat, Money Cur, Money Prev)>();
        foreach (var root in account.RootCategories)
        {
            var cur = SpentInTree(account, p, root.Id);
            if (cur.Amount <= 0m) continue;
            var prevAmt = prev is null ? Money.Zero(account.Currency) : SpentInTree(account, prev, root.Id);
            rows.Add((root, cur, prevAmt));
        }

        rows.Sort((a, b) => b.Cur.Amount.CompareTo(a.Cur.Amount));
        var max = rows.Count > 0 ? rows.Max(r => r.Cur.Amount) : 0m;

        var result = new List<CategorySpend>();
        for (var i = 0; i < rows.Count; i++)
        {
            var (cat, cur, prevAmt) = rows[i];
            var dir = DeltaDirection(cur.Amount, prevAmt.Amount);
            var bar = max > 0m ? cur.Amount / max : 0m;
            result.Add(new CategorySpend(cat.Name, cur, bar, dir, Palette[i % Palette.Length]));
        }
        return result;
    }

    private static DeltaDir DeltaDirection(decimal cur, decimal prev)
    {
        if (prev <= 0m) return cur > 0m ? DeltaDir.Up : DeltaDir.Flat;
        var change = (cur - prev) / prev;
        return change > 0.05m ? DeltaDir.Up : change < -0.05m ? DeltaDir.Down : DeltaDir.Flat;
    }

    // --- 6-period outgoings trend ---------------------------------------------------------------

    private static IReadOnlyList<TrendPoint> BuildTrend(IReadOnlyList<Period> periods, int idx, string currency)
    {
        var start = Math.Max(0, idx - 5);
        var slice = new List<Period>();
        for (var i = start; i <= idx; i++) slice.Add(periods[i]);
        var max = slice.Count > 0 ? slice.Max(p => p.ExpensesTotal.Amount) : 0m;

        return slice.Select(p => new TrendPoint(
            p.From.ToString("MMM", CultureInfo.InvariantCulture),
            p.ExpensesTotal,
            max > 0m ? p.ExpensesTotal.Amount / max : 0m,
            p == periods[idx])).ToList();
    }

    private static (bool Up, string Note) TrendNarrative(IReadOnlyList<TrendPoint> trend, Func<Money, string> fmt)
    {
        if (trend.Count < 2) return (false, "Not enough history yet to spot a trend.");
        var first = trend[0].Outgoings.Amount;
        var last = trend[^1].Outgoings.Amount;
        var diff = last - first;
        if (Math.Abs(diff) < 1m)
            return (false, "Your monthly outgoings are holding steady.");
        var up = diff > 0m;
        var money = fmt(new Money(Math.Abs(decimal.Round(diff, 2)), trend[^1].Outgoings.Currency));
        return up
            ? (true, $"You've spent {money} more per month than {trend.Count} months ago. Worth a second look.")
            : (false, $"You've trimmed {money} off your monthly outgoings since {trend.Count} months ago. Nice.");
    }

    // --- Signals --------------------------------------------------------------------------------

    private IReadOnlyList<Signal> BuildSignals(
        Account account, IReadOnlyList<Period> periods, int idx,
        decimal? savingsRate, decimal target, Func<Money, string> fmt)
    {
        var p = periods[idx];
        var prev = idx > 0 ? periods[idx - 1] : null;
        var warn = new List<Signal>();
        var good = new List<Signal>();
        var info = new List<Signal>();

        // Category spiking over its trailing average.
        var spike = TopSpikingCategory(account, periods, idx);
        if (spike is { } s)
            warn.Add(new Signal(SignalKind.Warn, $"{s.Name} is eating your budget",
                $"You've spent {fmt(s.Cur)} on {s.Name} — your recent average is {fmt(s.Avg)}.",
                $"+{s.Pct}%", DeltaDir.Up));

        // Overspent budgets.
        var (overCount, overAmount) = Overspends(account, p);
        if (overCount > 0)
            warn.Add(new Signal(SignalKind.Warn,
                overCount == 1 ? "A budget is overspent" : $"{overCount} budgets overspent",
                $"You're {fmt(overAmount)} over plan across {(overCount == 1 ? "one category" : $"{overCount} categories")} this month.",
                "over", DeltaDir.Up));

        // No savings set aside.
        if (p.SavingsNetTotal.Amount <= 0m)
            warn.Add(new Signal(SignalKind.Warn, "No savings set aside",
                "You haven't moved anything into savings this period. Even a small amount keeps the habit alive.",
                "—", DeltaDir.Flat));

        // Savings rate above target.
        if (savingsRate is { } r && r >= target)
            good.Add(new Signal(SignalKind.Good, "Savings on track",
                $"You set aside {Pct(r)} of what came in — at or above your {Pct(target)} goal.",
                $"{Pct(r)}", DeltaDir.Down));

        // A category that fell vs last month.
        if (prev is not null)
        {
            var drop = TopDroppingCategory(account, p, prev);
            if (drop is { } d)
                good.Add(new Signal(SignalKind.Good, $"{d.Name} spend down",
                    $"{fmt(d.Cur)} vs {fmt(d.Prev)} last month. Keep it up.",
                    $"−{d.Pct}%", DeltaDir.Down));
        }

        // End-of-period runway (only for the latest, still-open period).
        if (idx == periods.Count - 1 && p.Status == PeriodStatus.Open)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (today <= p.To && today >= p.From)
            {
                var daysLeft = p.To.DayNumber - today.DayNumber;
                info.Add(new Signal(SignalKind.Info, "Days left in the period",
                    $"You have {fmt(p.ExpectedClosingBalance)} on hand with {daysLeft} day{(daysLeft == 1 ? "" : "s")} to go.",
                    $"{daysLeft}d left", DeltaDir.Flat));
            }
        }

        // Deficit (overspent into the savings earmark).
        if (p.Deficit.Amount > 0m)
            warn.Add(new Signal(SignalKind.Warn, "Spending dipped into savings",
                $"{fmt(p.Deficit)} of this period's spend isn't backed by fresh cash — it leans on your savings earmark.",
                "deficit", DeltaDir.Up));

        // Priority: warnings first, then a positive, then info — capped at 5.
        var ordered = new List<Signal>();
        ordered.AddRange(warn);
        ordered.AddRange(good);
        ordered.AddRange(info);
        return ordered.Take(5).ToList();
    }

    private (string Name, Money Cur, Money Avg, int Pct)? TopSpikingCategory(
        Account account, IReadOnlyList<Period> periods, int idx)
    {
        if (idx == 0) return null;
        var p = periods[idx];
        (string Name, Money Cur, Money Avg, int Pct)? best = null;
        foreach (var root in account.RootCategories)
        {
            var cur = SpentInTree(account, p, root.Id).Amount;
            if (cur <= 0m) continue;

            var start = Math.Max(0, idx - 3);
            decimal sum = 0m; var n = 0;
            for (var i = start; i < idx; i++) { sum += SpentInTree(account, periods[i], root.Id).Amount; n++; }
            if (n == 0) continue;
            var avg = sum / n;
            if (avg <= 0m) continue;

            var pct = (int)Math.Round((cur - avg) / avg * 100m, MidpointRounding.AwayFromZero);
            if (pct < 25) continue; // only flag a real spike
            if (best is null || pct > best.Value.Pct)
                best = (root.Name, new Money(cur, account.Currency), new Money(decimal.Round(avg, 2), account.Currency), pct);
        }
        return best;
    }

    private (string Name, Money Cur, Money Prev, int Pct)? TopDroppingCategory(Account account, Period p, Period prev)
    {
        (string Name, Money Cur, Money Prev, int Pct)? best = null;
        foreach (var root in account.RootCategories)
        {
            var cur = SpentInTree(account, p, root.Id).Amount;
            var pr = SpentInTree(account, prev, root.Id).Amount;
            if (pr <= 0m || cur >= pr) continue;
            var pct = (int)Math.Round((pr - cur) / pr * 100m, MidpointRounding.AwayFromZero);
            if (pct < 10) continue;
            if (best is null || pct > best.Value.Pct)
                best = (root.Name, new Money(cur, account.Currency), new Money(pr, account.Currency), pct);
        }
        return best;
    }

    private (int Count, Money Amount) Overspends(Account account, Period p)
    {
        var count = 0;
        var amount = Money.Zero(account.Currency);
        foreach (var b in p.Budgets)
        {
            var spent = SpentInTree(account, p, b.CategoryId);
            if (spent > b.Allocated) { count++; amount += spent - b.Allocated; }
        }
        return (count, amount);
    }

    // --- Quick wins -----------------------------------------------------------------------------

    private IReadOnlyList<QuickWin> BuildQuickWins(
        Account account, Period p, decimal? savingsRate, Money income, decimal target, Func<Money, string> fmt)
    {
        var wins = new List<QuickWin>();

        // Worst overspent budget.
        (Budget B, Money Over)? worst = null;
        foreach (var b in p.Budgets)
        {
            var spent = SpentInTree(account, p, b.CategoryId);
            if (spent > b.Allocated)
            {
                var over = spent - b.Allocated;
                if (worst is null || over > worst.Value.Over) worst = (b, over);
            }
        }
        if (worst is { } w)
        {
            var name = account.FindCategory(w.B.CategoryId)?.Name ?? "that category";
            wins.Add(new QuickWin($"Rein in {name}: you're {fmt(w.Over)} over budget this month."));
        }

        // Below savings target.
        if (income.Amount > 0m && (savingsRate ?? 0m) < target)
        {
            var gap = (target - (savingsRate ?? 0m)) * income.Amount;
            if (gap > 0m)
                wins.Add(new QuickWin($"Set aside {fmt(new Money(decimal.Round(gap, 2), account.Currency))} more to hit your {Pct(target)} savings goal."));
        }

        // A meaningful category with spend but no budget.
        foreach (var root in account.RootCategories)
        {
            if (p.FindBudget(root.Id) is not null) continue;
            var spent = SpentInTree(account, p, root.Id);
            if (spent.Amount > 0m)
            {
                wins.Add(new QuickWin($"Give {root.Name} a budget — you've spent {fmt(spent)} with no plan in place."));
                break;
            }
        }

        return wins.Take(3).ToList();
    }

    private string SavingsCritique(decimal? rate, Money? shortfall, decimal target, Func<Money, string> fmt)
    {
        if (rate is null)
            return "No contributions recorded this period, so there's no savings rate to measure yet.";
        if (rate.Value >= target)
            return $"You saved {Pct(rate.Value)} this period — at or above your {Pct(target)} goal. Keep that rhythm.";
        var tail = shortfall is { } s ? $" That's about {fmt(s)} short of your goal this period." : "";
        return $"You saved {Pct(rate.Value)} this period — better than nothing, but short of your {Pct(target)} goal.{tail}";
    }

    private static string Pct(decimal ratio) =>
        $"{decimal.Round(ratio * 100m, 0, MidpointRounding.AwayFromZero)}%";
}
