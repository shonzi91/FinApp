using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Budgeting;
using FinApp.Domain.Common;
using FinApp.Domain.Periods;

// FinApp data seeder. Reads a CSV of rows and pushes them into a NEW account via the server API
// (auth -> create account -> build the aggregate with the domain -> PUT snapshot). The CSV->entity
// mapping lives in BuildAccount() and is the only part that changes per spreadsheet layout.
//
// Config via env vars (all optional, with dev defaults):
//   SEED_SERVER    server base url            (default http://localhost:5179)
//   SEED_USER      username to log in/register
//   SEED_EMAIL     email (only used on register)
//   SEED_PASS      password
//   SEED_ACCOUNT   new account name           (default "Imported")
//   SEED_CURRENCY  ISO currency               (default EUR)
//   SEED_CSV       path to the expenses CSV   (default sample-expenses.csv next to the exe/cwd)
//   SEED_REGISTER  "1" to register the user first (otherwise just log in)

static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;

var server = Env("SEED_SERVER", "http://localhost:5179").TrimEnd('/');
var user = Env("SEED_USER", "seed-user");
var email = Env("SEED_EMAIL", "seed-user@example.com");
var pass = Env("SEED_PASS", "seed-pass-1234");
var accountName = Env("SEED_ACCOUNT", "Imported");
var currency = Env("SEED_CURRENCY", "EUR");
var csvPath = Env("SEED_CSV", "sample-expenses.csv");
var doRegister = Env("SEED_REGISTER", "0") == "1";

using var http = new HttpClient { BaseAddress = new Uri(server) };

Console.WriteLine($"→ server {server}");

// 1) Auth ---------------------------------------------------------------------------------------
if (doRegister)
{
    var reg = await http.PostAsJsonAsync("/auth/register", new RegisterRequest(user, email, pass));
    Console.WriteLine($"  register {user}: {(int)reg.StatusCode} {reg.StatusCode}");
    // 409 (already exists) is fine — we fall through to login.
}

var login = await http.PostAsJsonAsync("/auth/login", new LoginRequest(user, pass));
if (!login.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"✗ login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
    return 1;
}
var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
Console.WriteLine($"  logged in as {auth.Username} ({auth.UserId})");

// 2) Create the account -------------------------------------------------------------------------
var createResp = await http.PostAsJsonAsync("/accounts", new CreateAccountRequest(accountName, currency));
if (!createResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"✗ create account failed: {(int)createResp.StatusCode} {await createResp.Content.ReadAsStringAsync()}");
    return 1;
}
var summary = (await createResp.Content.ReadFromJsonAsync<AccountSummaryDto>())!;
Console.WriteLine($"  created account “{summary.Name}” {summary.Id}");

// 3) Build the aggregate ------------------------------------------------------------------------
var familyJson = Env("SEED_FAMILY", "");
Account account;
if (familyJson.Length > 0)
{
    var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var fd = System.Text.Json.JsonSerializer.Deserialize<FamilyData>(File.ReadAllText(familyJson), jsonOpts)!;
    Console.WriteLine($"  family import: {fd.Months.Count} months from {familyJson}");
    account = BuildFamilyAccount(summary, fd, currency, auth.UserId, auth.Username);
}
else
{
    var rows = ReadCsv(csvPath);
    Console.WriteLine($"  read {rows.Count} rows from {csvPath}");
    account = BuildAccount(summary, currency, auth.UserId, auth.Username, rows);
}

// 4) Push the snapshot --------------------------------------------------------------------------
var current = (await http.GetFromJsonAsync<AccountSnapshot>($"/accounts/{summary.Id}/snapshot"))!;
var payload = AccountSnapshotSerializer.Serialize(account);
var put = await http.PutAsJsonAsync($"/accounts/{summary.Id}/snapshot", new SaveAccountRequest(payload, current.Version));
if (!put.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"✗ push snapshot failed: {(int)put.StatusCode} {await put.Content.ReadAsStringAsync()}");
    return 1;
}

// 5) Verify by reading it back ------------------------------------------------------------------
var saved = (await http.GetFromJsonAsync<AccountSnapshot>($"/accounts/{summary.Id}/snapshot"))!;
var back = AccountSnapshotSerializer.Deserialize(saved.Payload);
var expenses = back.Periods.SelectMany(p => p.Expenses).ToList();
var total = expenses.Aggregate(Money.Zero(currency), (s, e) => s + e.Amount);
Console.WriteLine($"✓ imported into account {summary.Id}:");
Console.WriteLine($"  {back.Periods.Count} periods, {back.Categories.Count} categories, "
                + $"{back.SavingCategories.Count} buckets, {back.Members.Count} members, {expenses.Count} expenses, total {total}");
foreach (var p in back.Periods)
    Console.WriteLine($"   {p.From:MMM} open={p.InitialTotal} contrib={p.ContributionsPaidTotal} "
                    + $"budget={p.BudgetedTotal} spent={p.ExpensesTotal} saved={p.SavingsNetTotal} close={p.ExpectedClosingBalance}");
return 0;

// --- CSV -> aggregate mapping (the per-spreadsheet part) ---------------------------------------
static Account BuildAccount(AccountSummaryDto summary, string currency, Guid userId, string userName,
    IReadOnlyList<Dictionary<string, string>> rows)
{
    var account = AccountSnapshotSerializer.CreateForHeader(
        summary.Id, summary.Name, currency, userId, new[] { (userId, userName) });
    account.AddDefaultFunds();

    Money M(decimal v) => new(v, currency);
    var defaultFund = account.RootFunds.First().Id;

    var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    Guid CategoryId(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Other" : name.Trim();
        if (!categories.TryGetValue(name, out var id)) { id = account.AddCategory(name).Id; categories[name] = id; }
        return id;
    }
    Guid FundId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return defaultFund;
        return account.Funds.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? defaultFund;
    }

    var parsed = rows
        .Select(r => new
        {
            Date = ParseDate(Get(r, "date")),
            Category = Get(r, "category"),
            Amount = ParseAmount(Get(r, "amount")),
            Fund = Get(r, "fund"),
            Note = Get(r, "note"),
        })
        .Where(x => x.Amount > 0)
        .ToList();

    // One period spanning the data range (single-period import; refine to per-month if desired).
    var dates = parsed.Where(p => p.Date is not null).Select(p => p.Date!.Value).ToList();
    var from = dates.Count > 0 ? new DateOnly(dates.Min().Year, dates.Min().Month, 1) : DateOnly.FromDateTime(DateTime.Today);
    var to = dates.Count > 0 ? dates.Max() : from.AddMonths(1).AddDays(-1);
    if (to < from) to = from;
    var period = account.StartPeriod(from, to);

    foreach (var p in parsed)
    {
        var date = p.Date ?? from;
        period.AddExpense(new Expense(CategoryId(p.Category), M(p.Amount), date, userId, FundId(p.Fund), NullIfEmpty(p.Note)));
    }

    return account;
}

// --- Family workbook -> aggregate (multi-period) -----------------------------------------------
static Account BuildFamilyAccount(AccountSummaryDto summary, FamilyData fd, string currency, Guid userId, string userName)
{
    var account = AccountSnapshotSerializer.CreateForHeader(
        summary.Id, summary.Name, currency, userId, new[] { (userId, userName) });
    Money M(decimal v) => new(v, currency);

    var fund = account.AddFund(fd.FundName ?? "Account").Id;

    // Contributors become members (deterministic fake user ids — the snapshot is opaque to the server).
    var members = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    foreach (var who in fd.Contributors ?? [])
    {
        var id = Guid.NewGuid();
        account.AddMember(id, who);
        members[who] = id;
    }
    var contribCats = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    foreach (var cc in fd.ContribCategories ?? [])
        contribCats[cc] = account.AddContributionCategory(cc).Id;

    // Budget categories (codes 1..8) + saving buckets.
    var cats = new Dictionary<int, Guid>();
    foreach (var (codeStr, nm) in fd.Categories ?? [])
        cats[int.Parse(codeStr)] = account.AddCategory(nm).Id;
    var buckets = (fd.SavingBuckets ?? []).Select(b => account.AddSavingCategory(b).Id).ToList();

    Period? prev = null;
    foreach (var m in (fd.Months ?? []).OrderBy(x => x.Index))
    {
        var days = DateTime.DaysInMonth(2026, m.Index);
        var from = new DateOnly(2026, m.Index, 1);
        var period = account.StartPeriod(from, new DateOnly(2026, m.Index, days));

        // Opening = explicit value (January) or the previous month's closing balance (carryover chain).
        var opening = m.Opening ?? prev?.ExpectedClosingBalance.Amount ?? 0m;
        period.SetInitialBalance(fund, M(opening));

        // Pre-existing bucket balances — first period only.
        if (m.SavingsInitial is { } init)
            for (var i = 0; i < buckets.Count && i < init.Count; i++)
                account.SetSavingInitialAmount(buckets[i], init[i]);

        foreach (var c in m.Contributions ?? [])
            period.Deposit(members.GetValueOrDefault(c.Who, userId), M(c.Amount),
                contribCats.GetValueOrDefault(c.Cat, contribCats.Values.FirstOrDefault()), fund, from);

        foreach (var (codeStr, amt) in m.Budgets ?? [])
            if (cats.TryGetValue(int.Parse(codeStr), out var cid) && amt > 0)
                period.AddBudget(cid, M(amt));

        // Savings allocated before expenses (so the earmark fits while the balance is highest). Positive only.
        for (var i = 0; i < buckets.Count && i < (m.SavingsSaved?.Count ?? 0); i++)
        {
            var amt = m.SavingsSaved![i];
            if (amt <= 0) continue;
            try { period.AllocateToSavings(buckets[i], M(amt), from); }
            catch (Exception ex) { Console.WriteLine($"   ! {m.Name} saving {amt} to bucket {i} skipped: {ex.Message}"); }
        }

        foreach (var e in m.Expenses ?? [])
        {
            var cid = cats.TryGetValue(e.Cat, out var c2) ? c2 : cats.Values.First();
            var day = Math.Min(Math.Max(e.Day, 1), days);
            period.AddExpense(new Expense(cid, M(e.Amount), new DateOnly(2026, m.Index, day), userId, fund, null));
        }

        prev = period;
    }
    return account;
}

static string Get(Dictionary<string, string> row, string key) => row.GetValueOrDefault(key, "").Trim();
static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

static DateOnly? ParseDate(string s) =>
    DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

static decimal ParseAmount(string s)
{
    s = s.Replace("€", "").Replace("$", "").Replace("£", "").Replace(",", "").Trim();
    return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
}

// Minimal CSV reader (handles quoted fields + commas inside quotes). Header row is lower-cased into keys.
static List<Dictionary<string, string>> ReadCsv(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException($"CSV not found: {Path.GetFullPath(path)}");
    var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
    if (lines.Count == 0) return [];
    var headers = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
    var result = new List<Dictionary<string, string>>();
    foreach (var line in lines.Skip(1))
    {
        var cells = SplitCsv(line);
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count && i < cells.Count; i++) row[headers[i]] = cells[i];
        result.Add(row);
    }
    return result;
}

static List<string> SplitCsv(string line)
{
    var cells = new List<string>();
    var cur = new System.Text.StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (c == '"') { if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else inQuotes = !inQuotes; }
        else if (c == ',' && !inQuotes) { cells.Add(cur.ToString()); cur.Clear(); }
        else cur.Append(c);
    }
    cells.Add(cur.ToString());
    return cells;
}

// --- family.json shape (produced by extract_family.py) -----------------------------------------
record FamilyData(
    string Account, string Currency, string? FundName,
    Dictionary<string, string>? Categories, List<string>? ContribCategories, List<string>? Contributors,
    List<string>? SavingBuckets, List<MonthData>? Months);
record MonthData(
    string Name, int Index, decimal? Opening, List<ContribData>? Contributions,
    Dictionary<string, decimal>? Budgets, List<decimal>? SavingsInitial, List<decimal>? SavingsSaved,
    List<ExpData>? Expenses);
record ContribData(string Who, decimal Amount, string Cat);
record ExpData(int Cat, decimal Amount, int Day);
