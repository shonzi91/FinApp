# Budgiely (FinApp) — session handoff

Last updated: 2026-06-30 (Session 12b). Read this + [README.md](README.md) + recent `git log` to catch up.
Product is now branded **TandemTab** ("Track together, save together.") — renamed from Budgiely in Session 11m.
Logo = a mint **TT / two-figures-on-a-beam** monogram (`Components/TandemLogo.razor`, was `BudgieLogo`). **Code
namespaces/assemblies stay `FinApp.*`** (product name ≠ assembly name — not worth a full rename). Live on Cloud Run, all
on `origin/main` (GitHub shonzi91/FinApp — **repo not yet renamed**; user to do it in Settings, then repoint the remote).

**Current state (2026-06-26):** live as **revision finapp-00032**; **104 tests pass** (80 domain + 5 persistence + 19 server).
Session 11 ran long with many sub-sessions (11a–11i below) — money-model reshaping + UI polish. Money model now:
*budgets are capped at `Current − savings + spent` (hard cap); savings is an advisory earmark (uncapped) and the only thing
that reserves cash; `Free to allocate = Current − savings`; fund→fund transfers are uncapped (total-preserving), only
sending money OUT of the account caps at the fund balance.* Settle-on-behalf, expenses calendar, and a per-account
"savings configuration" roadmap item all landed/queued this session. Two EF migrations added (AddExpenseOnBehalfOfOtherAccount,
AddExpenseSettlementLinks). Redeploy: `gcloud run deploy finapp --source . --region europe-west1`.

## Session 12 (2026-06-29) — Insights / financial-health tab (NEW roadmap item #2). UI-only, NO domain change. 106 tests.
New **5th tab "Insights"** on the Dashboard — a read-only financial-health report for the **currently-viewed period**
(respects period navigation). Built by adapting a dark "Finch" HTML mockup the user supplied into TandemTab's
mint/cream look. **Everything is derived from existing domain reads — no domain logic/storage/migrations changed**
(the user asked to be consulted before any domain change; none was needed for v1).
- **New `src/FinApp.Shared.UI/Services/InsightsService.cs`** — pure presentation-layer compute over the `Account`
  aggregate's public reads (mirrors how `BudgetingState` news-up `BudgetCoverageService`/`SavingsReportService`).
  It's **not in DI**; the Dashboard news it up (`private readonly InsightsService _insights = new();`). Produces a
  `FinancialHealthReport` record (+ `Signal`/`CategorySpend`/`TrendPoint`/`QuickWin` records and `DeltaDir`/`HealthBand`/
  `SignalKind` enums). `Build(account, periodIndex, Func<Money,string> fmt)` — the Dashboard passes its own `Fmt` so
  currency formatting matches exactly.
- **Health score (0–100)** = four equally-weighted 25-pt components: savings-rate-vs-target, budget adherence
  (overspend ÷ budgeted; **neutral 15 when nothing is budgeted**), living-within-means (deficit/closing), and spending
  trend vs trailing 3-period average (**neutral 15 when no history**). Bands: <40 at-risk (red), 40–69 average (amber),
  ≥70 healthy (green). Score delta vs the previous period drives the verdict copy. Formula lives only in
  `InsightsService.ComputeScore` — tweak there.
- **Sections:** semicircle SVG gauge (reuses the mockup geometry; `stroke-dashoffset` set inline, CSS-animated, **no JS**)
  + risk/avg/healthy needle bar; **Signals** (computed warn/good/info cards — category spike vs trailing avg, overspent
  budgets, no-savings-this-period, savings-on-track, a category that dropped, end-of-period runway, deficit; warns first,
  capped at 5); **Where it's going** (spend by **root** category, bar width ∝ max, ▲/▼ vs last period); **Savings rate**
  (period rate vs target, 0–40% track w/ goal marker, critique line); **6-period outgoings trend** (mini bar chart, current
  period highlighted); **Quick wins** (≤3 derived suggestions). Empty-state when the period has no income/expense/budget.
- **Savings-rate target is now a PER-ACCOUNT setting** (user-approved domain change, done this session): new
  `Account.SavingsRateTarget` (decimal fraction 0..1, default **0.20**) + `SetSavingsRateTarget` (validates 0..1). It's
  **body data — it rides in the snapshot serializer**, NOT the relational header: `AccountSnapshotSerializer`'s `AccountNode`
  carries it (default 0.20 so legacy snapshots back-fill), and `FinAppDbContext` does **`a.Ignore(x => x.SavingsRateTarget)`**.
  **Why ignore + no migration:** prod Postgres on Cloud Run inits via **`EnsureCreated()`** (Program.cs ~L123), which never
  ALTERs an existing table — a mapped column would make the server's `db.Accounts` SELECT reference a non-existent column and
  crash. Keeping it in the opaque body sidesteps that entirely (the server already treats the body as opaque). `InsightsService`
  reads `account.SavingsRateTarget`; `BudgetingState` exposes `SavingsRateTarget` + `SetSavingsRateTarget` and `AddAccount`
  takes an optional target. UI: a **"Savings target (%)"** number input in the **New account** and **Edit account** modals
  (the rename modal is now "Edit account"). Tests: `SavingsTargetTests` (domain: default/set/validation) + serializer
  round-trip + a legacy-snapshot-defaults-to-20% test. **111 tests** (84 domain + 21 server + 6 persistence).
- The mockup's "subscriptions" card and "emergency fund" concept were dropped (app has no recurring-expense model; savings
  buckets are generic) — replaced by the generic "no savings set aside" signal.
- **Files:** new `Services/InsightsService.cs`; `Domain/Accounts/Account.cs`; `Contracts/AccountSnapshotSerializer.cs`;
  `Persistence/FinAppDbContext.cs`; `Pages/Dashboard.razor` (nav button + `Tab.Insights` + tabpanel + `_insights`/`_fSavingsTarget`
  fields + `PctText` + account-modal wiring); `Pages/Dashboard.razor.css` ("INSIGHTS TAB" block, mint/cream); `Services/BudgetingState.cs`;
  `Services/Localizer.cs` (BG strings — **generated insight/win/verdict sentences stay EN-only**, like other deep bodies).
- **Possible follow-ups:** add an InsightsService unit test (no test project covers Shared.UI today); localize the generated
  sentences; a "How it's calculated" expander for the score; the savings gauge track is fixed at 0–40% (clamps if target > 40%).

## Session 12e (2026-06-30) — budgets-tab dashed rings, modal nav, avatars in lists, full i18n, flag picker. UI-only. 112 tests.
Seven requests, all presentation-layer (no domain/serializer/EF changes):
1. **Budgets tab shows non-budgeted categories** as dashed rings (like goal-less savings buckets): any category with spend
   but no budget (and no budgeted child) gets a dashed mint ring showing spent + "no budget". New `BudgetingState.SpentInCategory`.
2. **Removed the 🧾 in budget rings** (Add-expense lives in the category-detail modal) and **removed "Sub-category" from the
   category-detail modal** (it's in the Edit/budget modal now — last session).
3. **Modals no longer close on outside click** — dropped `@onclick="CloseModal"` from the Dashboard `.modal-backdrop` and the
   MainLayout `.pm-backdrop`. (The language dropdown's backdrop still closes — it's a popup, not a modal.)
4. **Cancel/Close steps back to the parent modal** when there is one. New `_modalBack` `Stack<Action>` + `Back()`; all Cancel/Close
   buttons now call `Back` (empty stack → full `CloseModal`, which clears the stack). Wrappers push a "reopen parent" action:
   `DetailEdit/DetailAddExpense/DetailDelete/DetailEditExpense/DetailDeleteExpense` (from category-detail) and
   `EditAddSub/EditEditSub/EditDeleteSub` (from edit-category). The overlay blocks page clicks, so the stack is always empty at a
   fresh page-level open — no need to clear it on open.
5. **Avatars in the contributions list** — new reusable **`Components/Avatar.razor`** (loads localStorage `finapp-avatar:{name}`
   → photo if it's this device's user, else a colour-from-name initial). Used next to each member name; also reusable for the app bar.
6. **Full i18n pass** — wrapped ~all remaining hard-coded English in `Dashboard.razor` (labels, modal titles, hints, checkboxes,
   button text, and `title=` tooltips) in `@Loc[...]` and added BG translations (~50 keys). Razor supports nested quotes in
   `title="@Loc["x"]"`. Generated insight/win sentences in `InsightsService` remain EN (dynamic; not Loc-wrapped). To find gaps:
   `grep -nE '<(label|h3|h4)>[^@<]' Dashboard.razor` and `grep -oE 'title="[A-Z][^"@]*"'`.
7. **Language picker shows flags again** — `Localizer.Languages` is now `(Code,Name,Flag)`; the 🌐 trigger shows the selected
   flag + each menu row shows flag+name. ⚠️ **Flag emoji render as bare letters ("GB"/"BG") on Windows desktop** — this was the
   earlier "(BG/EN)" complaint; re-added per request, but if it looks wrong on desktop, swap to SVG/`img` flags.
- **Razor gotcha (again):** EditCat's `var subs = …;` is bare inside the `@switch`/`case`; the budgets `@{ var budgeted…; var unbudgeted…; }` is fine (markup context inside the tabpanel).
- **Files:** `Shared.UI/Pages/Dashboard.razor` (rings, modal nav, i18n, avatars), `Shared.UI/Services/{BudgetingState,Localizer}.cs`,
  `Shared.UI/Components/{Avatar.razor(+css),LanguagePicker.razor(+css)}`, `Shared.UI/Layout/MainLayout.razor`. 112 tests.

## Session 12d (2026-06-30) — icons everywhere, sub-cat editing, avatar, language dropdown, smarter spike. 112 tests.
Five requests:
1. **Edit-category modal now lists sub-categories** with ✏️/🗑️ (→ existing edit/delete flows) + a "➕ Sub-category" action.
2. **Icons for funds, savings buckets, AND contribution categories** (was categories-only). Same body-data pattern as
   `Category.Icon`: new `Icon` + `SetIcon` on `Fund`/`SavingCategory`/`ContributionCategory` (NOT ctor params — EF binding),
   carried in the snapshot serializer (each node's `Icon`, default null), **`Ignore`d in EF**. `Account.SetFundIcon`/
   `SetSavingCategoryIcon`/`SetContributionCategoryIcon`. `BudgetingState` gained `FundIcon/SavingBucketIcon/ContributionCategoryIcon`
   (effective) + `…StoredIcon` (raw) + icon params on add/save. Shared **`iconPicker` RenderFragment** (reads `_fName`/`_fIcon`)
   drops into every add/edit modal (categories, funds, buckets; contrib uses an inline copy keyed off `_contribCatName`). Icons
   shown in: Funds list (replaced the generic 🏦), Savings rings (big centred, like budgets), contributions list + manage list.
   `CategoryIcons` got generic `Effective(icon, name)`, +income/cash keywords (salary→💼, cash→💵…), +10 palette icons.
3. **Profile picture** — client-side only (localStorage `finapp-avatar:{username}`, never sent to the server). New JS
   `finappPickImage` (file-pick → canvas cover-crop to 128px → JPEG dataURL) in **both** index.html hosts. Shown as a round
   avatar in the app bar (initial-letter fallback) + Upload/Remove in the profile modal (`MainLayout`). NOTE: device-local, no
   cross-device sync — making it sync is a server/User change (avatar column or blob endpoint) deferred for the prod-EnsureCreated reason.
4. **Language switch → dropdown, icon-only** — flag emoji removed (they render as bare "GB"/"BG" letters on Windows!). New
   **`Components/LanguagePicker.razor`** (🌐 globe button → menu of language *names*; backdrop closes it) used in the app bar and
   AuthPanel. `Localizer.Languages` is now a `(Code,Name)[]` list (add a row + a Bg-style map to add a language); validation is
   list-driven. Removed the `.lang`/`.flag` markup + `SetLang`.
5. **"Eating your budget" spike made honest** — renamed to **"{cat} is running high"** and `TopSpikingCategory` now filters out
   low-base illusions: requires ≥40% over the trailing avg **AND** the absolute jump ≥10% of the month's spend **AND** the
   category ≥15% of spend, and **skips anything within its budget** (planned spend isn't flagged). Ranked by money, not %.
- **Razor gotcha (again):** the EditCat sub-cat `var subs = …;` must be bare inside the `@switch`/`case` body (no `@{ }`).
- **Files:** domain `Funds/Fund.cs`, `Savings/SavingCategory.cs`, `Periods/ContributionCategory.cs`, `Accounts/Account.cs`;
  `Contracts/AccountSnapshotSerializer.cs`; `Persistence/FinAppDbContext.cs`; `Shared.UI/Services/{CategoryIcons,BudgetingState,
  InsightsService,Localizer}.cs`; new `Shared.UI/Components/LanguagePicker.razor`(+`.css`); `Shared.UI/Layout/MainLayout.razor`(+`.css`);
  `Shared.UI/Components/AuthPanel.razor`; `Shared.UI/Pages/Dashboard.razor`; both `wwwroot/index.html`. Serializer test asserts the 3
  new icons round-trip. 112 tests.

## Session 12c (2026-06-30) — Overview tab + Insights/Budgets polish. UI-only. 112 tests.
Six requests, all UI-layer (no domain/serializer/EF changes):
1. **Budgets rings redesigned:** bigger cards (`.ring-card-lg` 150px → fewer per row) with the category **icon big & centered
   inside the ring**, the name beneath it, and the 🧾 add-expense button beneath that — all inside the circle. Spent/budgeted
   stays just below. New `.ring-ico-big`.
2. **Period nav:** removed the `(n/n)` count from the date button; arrows are now round chevron buttons (`‹`/`›`, restyled `.nav`).
3. **Savings-rate bar is now 0–100%** (was 0–40%) so low rates read honestly; the goal marker sits at the target %. The
   **score's savings component is less forgiving** — `InsightsService.ComputeScore` blends `0.6×(rate/target) + 0.4×min(1,rate)`,
   so hitting a 20% target no longer maxes that 25-pt component (need high absolute rates for full marks).
4. **Overspent-budgets signal is expandable** — `Signal.Details` (optional list) renders a `<details>`; the overspent card lists
   each category `icon name — €X over (spent / budget)`, worst first (`OverspentBudgets` helper).
5. **Spending trend reworked & monthly-normalized:** each period's spend is scaled to a whole month
   (`MonthlySpend = spend / (days+1) × 30.44`) so uneven period lengths compare fairly. New bar chart (`.trend-plot`/`.trend-bar`)
   with a dashed **average reference line** (`.trend-avg`) and month labels; the note compares the latest month to the
   rolling N-month average. Report gained `TrendAverage` + `TrendAvgFraction`.
6. **New "Overview" tab (now the default landing on desktop):** at-a-glance dashboard — summary cards (Current/free-to-allocate,
   Saved+rate, Spent, and a clickable **Health score** card → Insights), **Needs-your-attention** (warning signals, reusing a shared
   `RenderFragment<Signal> signalCard` so the overspent expander works here too), **Overspent budgets** as red rings, **Quick wins**,
   and **Top spending** (top 5 categories). Empty-state when no data. Phone init still opens Expenses first (unchanged).
   `Tab` enum gained `Overview` (first); `_tab` defaults to it.
- **Razor gotcha (re-confirmed):** inside an `@if{}`/`else{}` code block, a `var x = …;` must be **bare** — `@{ }` there is RZ1010
  ("Unexpected { after @"). Inside a markup element (`<div>…`), `@{ }` is correct. Bit me on the Overview `overspent` local.
- **Files:** `Shared.UI/Services/InsightsService.cs` (score, trend, overspent details, report fields), `Shared.UI/Pages/Dashboard.razor`
  (Overview tab + signalCard fragment + ring/period/savings/trend markup), `Shared.UI/Pages/Dashboard.razor.css` (rings, `.nav`,
  trend chart, expander, Overview), `Shared.UI/Services/Localizer.cs` (BG strings). No tests added (Insights is UI-layer, untested);
  112 tests still green.

## Session 12b (2026-06-30) — distinctive category icons + picker. UI + body-data domain field. 112 tests.
Categories now carry a display **icon** (emoji) so they're scannable at a glance.
- **Domain:** `Category.Icon` (string?, nullable) + `SetIcon` (trims; blank → null); `Account.AddCategory(name, parentId, icon)`
  gained an optional icon arg + `Account.SetCategoryIcon(id, icon)`. **Icon is NOT a constructor param** — EF binds entity
  constructors by matching params to mapped properties, and Icon is `Ignore`d, so a ctor param made EF reject the ctor
  ("No suitable constructor for Category"). Set it post-construction instead (same lesson applies to any future Ignored field).
- **Body data, like `SavingsRateTarget`:** rides in the snapshot (`AccountSnapshotSerializer.CategoryNode.Icon`, default null
  for back-compat), **`Ignore`d in EF** (`FinAppDbContext` Category config) → no column, no migration, safe for prod's EnsureCreated.
- **Pre-existing categories get icons free:** new `Shared.UI/Services/CategoryIcons.cs` — a **Palette of 36 emoji** for the picker
  + a name-keyword **`Guess`** (food→🍽️, rent→🏠, car→🚗, …, fallback 🏷️) + `Effective(category)` = explicit icon ?? guess.
  So categories with no stored icon still render a sensible one with zero data migration; users override via the picker.
- **UI:** add/edit-category modals have an **icon picker** (`.icon-grid`, 8-col / 6 on phones; first "auto" chip = clear to
  null = use the guess, and it previews the guess for the typed name). Icons now show on: Budgets ring cards, category-detail
  modal title, Expenses-tab rows, all category `<select>`s (new `CatOption(id,name,depth)` helper replaced `IndentLabel` at the
  4 category selects — `IndentLabel` now unused), and the Insights "Where it's going" breakdown (`CategorySpend.Icon`).
  `BudgetingState`: `CategoryIcon(id)` (effective), `CategoryStoredIcon(id)` (raw, for the edit picker), `AddCategory(...,icon)`,
  `EditCategory(id,name,icon)` (rename+icon in one save). Starter categories seed with icons (Food 🍽️ / Bills 💡 / Transport 🚗 / Other 🏷️).
- **Files:** `Domain/Budgeting/Category.cs`, `Domain/Accounts/Account.cs`, `Contracts/AccountSnapshotSerializer.cs`,
  `Persistence/FinAppDbContext.cs`, new `Shared.UI/Services/CategoryIcons.cs`, `Shared.UI/Services/BudgetingState.cs`,
  `Shared.UI/Services/InsightsService.cs`, `Shared.UI/Pages/Dashboard.razor`(+`.css`), `Shared.UI/Services/Localizer.cs`
  (Icon / Auto BG strings). Tests: `CategoryAdminTests` (icon default/set/clear) + serializer round-trip asserts icon + null.
  **112 tests** (85 domain + 21 server + 6 persistence).

## Session 11 (2026-06-25) — 8 UX/feature requests (on `main`, all 101 tests green)
Eight items from live use. **101 tests pass** (77 domain + 5 persistence + 19 server; +1 new domain test for #8).
New EF migration **`AddExpenseOnBehalfOfOtherAccount`** (single bool column; applies on start via `Migrate()`).
1. **Expense "on behalf of another account" + settle later.** New **persisted** flag `Expense.OnBehalfOfOtherAccount`
   (ctor param, `ExpenseNode` field default `false`, EF `Property` + migration, preserved through `Period.EditExpense`).
   Add-expense modal has a checkbox; flagged rows show a 🤝 button by the amount → **Settle modal** (amount + dest
   account + note). `BudgetingState.SettleExpenseToAccount`: records the amount as an **expense in the dest account**
   (its first category/fund, note "From {thisAccount}") **and** a matching **reimbursement deposit** back here (into the
   expense's fund, under an auto-created "Reimbursement" contribution category via `FindOrCreateContributionCategory`).
   Net: this account's cost drops by the settled amount, the other account bears it, original expense stays as the record.
   **Decision (told the user):** modeled as "the other account incurs it + reimburses you" using existing deposit/expense
   primitives — *not* a reduce-and-reattribute. Flip if wrong.
2. **Fund shown on expense rows** — Expenses-tab row reads `Category · Fund · 💰saving · 🤝 on behalf · note`.
3. **Icon on savings-activity movements** — movement rows get a leading ➡️ (move-to-budget) / 🔁 (bucket transfer) via
   `MovementIcon(SavingAllocation)`, matching the 💰 on deposit rows.
4. **Destination-fund picker on cross-account transfers** — both the inline "Transfer money" form and the per-fund
   Transfer modal lazy-load the dest account's funds (`BudgetingState.LoadAccountFundsAsync`, cached in `_destFunds`) and
   show a fund `<select>` when the target is another account. `TransferToAccount(..., Guid destinationFundId = default)`
   deposits into the chosen fund (`ResolveDestinationFund` falls back to first). `@bind:after` handlers load on dest change.
5. **Cache invalidation on between-account ops** — `TransferToAccount` + `SettleExpenseToAccount` now
   `_cache.Remove(destinationAccountId)` so switching to that account refetches from the DB (our own SignalR
   `AccountChanged` is ignored, so the dest entry would otherwise stay stale).
6. **Expenses tab defaults to today** — `ShowExpensesTab()` sets `_dayView` to today (clamped to the period); `ResetPickers`
   now **preserves** `_dayView` (re-clamps instead of nulling); `AddExpenseFromModal` sets `_dayView = _expenseDate` so it
   stays on the day just used. Tab button + phone-init both call `ShowExpensesTab`.
7. **Header restructured** — account dropdown moved to its own `.acct-bar` row, **out** of the arrow-flanked
   `.period-nav`; `.acct-select` restyled as a gradient pill with a custom `▾` caret (`.acct-picker::after`).
8. **Budget adjustment on copy-forward** — Start-next-month has an "Adjust budgets to this period's spending" checkbox
   (default on when copying). `Account.StartPeriod(..., bool adjustToConsumption)` → `AdjustToConsumption`:
   `⌈((budgeted + spent)/2)/10⌉ × 10` (halfway to actual spend, rounded **up** to the next 10). e.g. 400/470→440, 250/100→180.
   Threaded via `BudgetingState.StartNextPeriod(copyBudgets, openings, adjustBudgets)`.
**Files:** `Domain/Budgeting/Expense.cs`, `Domain/Periods/Period.cs`, `Domain/Accounts/Account.cs`,
`Contracts/AccountSnapshotSerializer.cs`, `Persistence/FinAppDbContext.cs` + new migration, `Shared.UI/Services/BudgetingState.cs`,
`Shared.UI/Pages/Dashboard.razor`(+`.css`), `Shared.UI/Services/Localizer.cs` (BG strings), `Domain.Tests/AccountPeriodTests.cs`.
Deployed as **finapp-00022** (`gcloud run deploy finapp --source . --region europe-west1`).

### Session 11b — savings caps reserve TOTAL accumulated savings (not just this period). 102 tests (78 domain).
Bug from live use: "Available to save / transfer / budget" only subtracted **this period's** net savings, so money saved
in earlier periods (now sitting in the carried-over opening balance) looked freshly allocatable — you could re-budget or
re-save it. Fix: all three caps now reserve the **whole accumulated savings** (incl. pre-app initial balances). Since the
caps live in `Period` (which can't see sibling periods), the relevant members take an optional `Money? priorSaved` arg
(default `null`/zero → unchanged for existing tests). New `*After(priorSaved)` variants:
`AvailableToSaveAfter`, `MaxAdditionalSavingsAfter`, `AvailableToTransferOutAfter`, `AvailableToTransferOutFromFundAfter`,
plus `MaxBudgetFor(categoryId, priorSaved)`; `AllocateToSavings`/`EditSavingDeposit`/`SetBudget`/`TransferOut` gained the
optional arg. `BudgetingState.PriorSaved = SavingsReportService.AccumulatedTotal(account) − Period.SavingsNetTotal`
(prior periods + initial), passed at every save/budget/transfer call site and the read members. **New "Available to budget"
hint** on the Add/Edit-category modals (`State.MaxBudgetFor`). Test: `Prior_period_savings_are_reserved_and_not_re_allocatable`.

### Session 11c — settle-on-behalf redesigned (reduce source + linked dest expense, bidirectional). 103 tests (79 domain).
Reworked feature #1 per live feedback. **Old** model (reimbursement deposit) is gone. **New** model:
- Settling pushes a chosen amount onto another account as a real **expense there** (pick **destination fund + category**),
  and **reduces the source expense** by that amount. Both carry a shared `Expense.SettlementId`; the source also stores
  `SettledToAccountId` + `SettledAmount` (its `Amount` is the reduced value; `OriginalAmount = Amount + SettledAmount`),
  the destination stores `SettledFromAccountId`. New EF migration **`AddExpenseSettlementLinks`** (4 cols; `SettledAmount`
  is a plain **decimal**, not Money — a nullable `Money?` ctor param can't bind in EF). Serializer `ExpenseNode` extended.
- **Domain:** `Period.SetSettlement(expenseId, settlementId, toAccountId, settledAmount)` reduces/​restores (amount 0 = unsettle,
  recomputes from `OriginalAmount` so re-settling is idempotent). `Period.EditExpense` carries all settlement fields forward.
- **Bidirectional sync** (`BudgetingState`, via a shared `MutateOtherAccountAsync` helper that loads/saves/​invalidates another
  account): `SettleExpenseToAccount(src, destAcct, destFund, destCat, amount, note)` upserts the dest expense + reduces source;
  `UnsettleExpense` removes the dest expense + restores source; editing the **destination** expense's amount mirrors back to the
  source (`SyncSourceSettlementAmount`), deleting the **destination** un-settles the source, deleting the **source** drops the
  linked dest expense (`RemoveLinkedSettlementExpense`). `EditExpense`/`RemoveExpense` are now async and do this propagation.
- **UI:** settle modal gained dest-fund + dest-category pickers (loaded via `LoadAccountStructureAsync` into `_destFunds`/`_destCats`)
  and an **Unsettle** button when editing. Source rows show a `🤝 €X → Account` tag (reduced amount displayed); destination rows
  show `↩ from Account`. The **"On behalf of another account" checkbox is hidden when the user has no other same-currency account**.
- Test: `Settling_an_expense_reduces_it_and_unsettling_restores_it`. (78→79 domain.)

### Session 11d — money model loosened to ADVISORY (user feel-test; may be reverted). 103 tests.
After a design discussion the user asked to try a softer model. Rule of thumb now: **block only what's physically
impossible; everything else warns.** This is a self-contained commit — `git revert` it if it doesn't feel right.
- **Budgets & savings no longer hard-cap.** Removed the throws in `Period.SetBudget`, `AllocateToSavings`,
  `EditSavingDeposit`. Over-allocating is allowed and surfaced as a **negative "free to allocate"**.
- **External transfer no longer blocks on the savings earmark** — `Period.TransferOut` keeps only the physical
  `amount > FundBalance` block; dipping into savings is allowed and the UI warns ("⚠ This dips into money earmarked for savings").
- **New advisory reads:** `Period.FreeToAllocateAfter(priorSaved)` and `FreeToBudgetForAfter(categoryId, priorSaved)`
  (both **unclamped** — go negative); `BudgetingState.FreeToAllocate` / `IsOverAllocated` / `FreeToBudgetFor`.
  The `*After`/`MaxBudgetFor`/`MaxAdditionalSavings` clamped helpers stay for display.
- **UI:** Current card gains a "€X free to allocate" sub-line (red when negative); budget Add/Edit hints show the
  unclamped free figure + "Over-allocated — allowed, just a heads-up."; transfer forms cap at the fund balance and
  warn (not disable) when dipping into savings (`InlineTransferDipsSavings` / `MTransferDipsSavings`).
- Tests updated from "throws" to advisory assertions (`Over_allocating_..._is_advisory_not_blocked`,
  `Saving_past_the_unallocated_cash_is_advisory_not_blocked`, `Editing_a_savings_deposit_past_the_cash_is_advisory_not_blocked`,
  `Transfer_out_dipping_into_savings_is_allowed_up_to_the_fund_balance`, `Saving_conversion_adds_to_a_budget`, and the prior-savings test).
  **Kept hard:** can't move/send more than a fund physically holds (`TransferFunds`/`TransferOut` fund-balance check). Expenses were already uncapped.
- **Fix (same session): "free to allocate" was double-counting spending.** It subtracted the *full* budget AND the
  spend (which is already in the closing balance). Now uses **unspent** budgets only: new `Period.RemainingBudgetTotal`
  = Σ `max(0, allocated − spent)` per category, and `FreeToAllocateAfter = closing − RemainingBudgetTotal − savings −
  priorSaved`. Removed `MaxBudgetFor` / `FreeToBudgetForAfter` (per-category headroom); the budget modals now show the
  single global `FreeToAllocate`. Test: `Free_to_allocate_counts_spending_once_not_twice` (€450 closing, €600 budget,
  €550 spent → €400 free, not −€150). 80 domain / 104 total.
- **Then simplified further (user): budgets reserve nothing; savings is the only earmark.** "Free = Current − savings"
  (no budget term at all — budgets are advisory, shown only via per-category coverage bars). `FreeToAllocateAfter =
  closing − SavingsNetTotal − priorSaved`; `AvailableToSaveAfter = closing − priorSaved` (dropped `− BudgetedTotal`), so
  `MaxAdditionalSavings == FreeToAllocate` (clamped) and the "Available to save" hint agrees with the Current-card free
  figure. Removed `RemainingBudgetTotal`. Over-allocation now only means savings > cash. Tests realigned
  (`Free_to_allocate_is_cash_minus_savings_ignoring_budgets`, etc.). Note: `Period.SetBudget` still takes a vestigial
  `priorSaved` param (no longer used) — left to avoid churn.

### Session 11g — UI polish: tab-switch flicker, budget nesting, expenses calendar (UI-only).
1. **Tab-switch flicker fixed** by not tearing down tab content: the `@switch (_tab)` became four always-mounted
   `<div class="tabpanel" hidden="@(_tab != Tab.X)">` panels inside `<div class="tab-content">` (min-height 55vh,
   `.tabpanel[hidden]{display:none}`). No DOM rebuild on switch (BudgetTreeNode/expense list/calendar stay mounted).
2. **Budget tree nesting clearer:** `BudgetTreeNode` indents the whole `.tree-lead` by `Depth*20px` (was a 16px margin on
   the name only), adds a `↳` twig for children, mutes child names (`.tree-name-child`), and tints nested rows
   (`.tree-row.tree-child` — faint bg + inset left guide).
3. **Expenses calendar view:** Expenses tab has a **List/Calendar** toggle (`_calendar`, ☰/📅 in the panel head). Calendar
   = a Mon–Sun month grid over the period (`CalendarDays()` pads to whole weeks), each in-range day shows its spend total
   (`byDay` dict) and is clickable → `OpenDayFromCalendar` focuses that day in the list. Out-of-range days greyed, today
   outlined, selected day highlighted. New `.cal-*` CSS. Razor gotcha hit + fixed: build the cell's class in a `var cls`
   local — inline `class="cal-cell@(...)"` with `""` string literals inside a double-quoted attribute breaks the parser.

### Session 11h — tab layout shift + uncapped intra-account fund transfers.
1. **Budgets-tab sideways shift fixed:** the taller tab added a scrollbar → the centered `.dash` jumped. Added
   `html { scrollbar-gutter: stable; overflow-y: scroll; }` to `App.Web/wwwroot/css/app.css` so the gutter is always reserved.
2. **Fund→fund transfers are now uncapped** (total-preserving, a fund may go negative). Removed the `amount > FundBalance`
   throw in `Period.TransferFunds`; **`TransferOut` (money leaving the account) still caps at the fund balance.** UI:
   `InlineTransferMax`/`MTransferMax` cap only when the destination is another account; intra-account = `decimal.MaxValue`.
   Test updated: `Internal_transfer_can_overdraw_a_fund_total_is_preserved` (Bank 100 → move 150 → Bank −50, Cash 150,
   closing still 100). 80 domain / 104 total.
3. **app.css was browser-cached** (linked without a fingerprint), so the scrollbar-gutter fix hadn't reached users —
   bumped the link to `css/app.css?v=2` (index.html is no-cache, so it re-fetches). Bump the query again for future
   global-CSS changes, or fingerprint `app.css` properly.
4. **Fund icon 🏦 now sits to the RIGHT of the fund name** everywhere (Funds panel, contributions, transfer log, budgets
   expense list). Expenses-tab rows use the format **`Category ⟵ Fund 🏦`** (`.exp-arrow` styles the ⟵). Budgets-tab
   expense rows now show the fund too (`FundName 🏦`).

### Session 11i — budget cap re-added (corrected), arrow in budgets list, list/calendar toggle restored.
1. **Budgeting is capped again — but at the right ceiling.** The old cap double-penalized spending (`budgeted+saved ≤
   closing`, which already nets spend). New **hard** cap in `Period.SetBudget`: `othersBudgeted + allocated ≤
   BudgetCeilingAfter = Current + Spent − savings` (= all your money minus savings; spending, being the realization of a
   budget, doesn't lower headroom). New `Period.BudgetCeilingAfter` + `MaxBudgetFor` (re-added) → `BudgetingState.MaxBudgetFor`;
   budget Add/Edit modals show "Available to budget: X". **Savings stays advisory (uncapped); only budgets are capped.**
   Example (user's): current 1000, saved 500, spent 1000 → ceiling 1500. Test: `Budget_is_capped_at_current_minus_savings_plus_spent_savings_stays_advisory`.
2. **Budgets-tab expense rows** now use the same `⟵` arrow before the fund (`date ⟵ Fund 🏦`); `.exp-arrow` added to
   `BudgetTreeNode.razor.css`.
3. **List icon restored** next to the calendar: the Expenses panel head has a ☰/📅 toggle again (`ShowDayList` /
   `ShowCalendarView`); default view is still today's day list. The per-day 🧾 add button in the calendar stays.

### Session 11j — small UX batch (UI-only).
- **Free-to-allocate hidden on closed periods** (Current card sub-line guarded by `IsPeriodOpen`).
- **Logo loaders:** initial load shows a bobbing `BudgieLogo` + "Loading…"; the Saving pill shows a small spinning budgie
  (scoped CSS uses `::deep .budgie-logo`; reuses `budgie-bob`, adds `budgie-spin`).
- **Budget hint simplified** — dropped the "(your money minus savings…)" parenthetical; just "Available to budget: X".
- **Expenses views:** opening the tab defaults to **today's day view** (`ShowExpensesTab` → `_dayView = today`); the ☰
  List button (`ShowDayList` → `_dayView = null`) shows the **grouped all-dates list** (clickable date headers → `GoToDay`
  drills into the day view). Day view (◀▶) is the drill-in; 📅 → calendar. Panel head shows "All expenses" in grouped mode.
- **Fund opening inputs accept `+`/`−` expressions** (e.g. `100+50-20`): inputs are `type=text`, evaluated by new
  `EvalSum(string)`; applies to the Start-next-month per-fund openings and the Add/Edit-fund opening field.
- **Period dates: removed the 📅 button; the period label itself is now the clickable button** (`.period-btn` → `OpenEditPeriod`).
- **Excel export per account** — done in Session 11k below (import still pending).

### Session 11k — Excel export per account (server-side, one sheet per period). 106 tests (21 server).
Added `ClosedXML` to `FinApp.Server`; new `AccountExportService` + `GET /accounts/{id}/export` (contributor-only) builds
an "Account" overview sheet + a sheet per period. Client downloads via `FinAppApiClient.ExportAccountAsync` → JS
`finappDownloadFile`; 📊 button in the account-ops bar. `ExportApiTests` validates a real xlsx is produced.
**Import is the remaining half** — see the roadmap entry (decide replace-vs-merge + id alignment).

### Session 11n — circular rings for budget categories & savings buckets. UI-only.
New reusable **`Components/ProgressRing.razor`** (+`.css`): SVG ring (track + arc via `stroke-dasharray`, `rotate(-90)`),
centered `ChildContent`. **Convention: solid arc = progress toward a target; `Dashed=true` = full dashed ring = "no target
set" (open)** — that's how goal-less savings buckets and budget-less categories stay visually consistent.
- **Budgets tab:** replaced the `BudgetTreeNode` tree with a `.ring-grid` of category rings (iterates `CategoryOptions`,
  flattened, children tagged `↳ Parent`). Center = category name (button → new `Modal.CategoryDetail`) + 🧾 add-expense;
  below = `spent / budgeted` (or "no budget" → dashed muted ring). **`Modal.CategoryDetail`** (`OpenCategoryDetail`) has
  Edit/budget · Sub-category · Add expense · Delete buttons + the category's expense list (edit/remove each). `BudgetTreeNode`
  is now **unused** (file kept; safe to delete later).
- **Savings tab:** bucket list → `.ring-grid`. Goal bucket = progress ring (warn near threshold, ✓ when reached); no-goal
  bucket = dashed mint ring. Center = name (→ edit); 💰➡️💸 row below; `saved / goal` (or just `saved`) below.
- CSS: `.ring-grid/.ring-card/.ring-name/.ring-add/.ring-actions/.ring-label/.ring-sub/.detail-actions` in `Dashboard.razor.css`
  (scoped ChildContent like `.ring-name` carries the Dashboard scope, so no `::deep` needed). No domain/test changes.

### Session 11m — renamed Budgiely → TandemTab + new logo. UI-only.
Logo component `BudgieLogo.razor` → **`TandemLogo.razor`** (git mv), SVG replaced with the chosen **TT monogram** (two heads
on a shared beam = two figures / two T's), mint gradient. Updated all `<BudgieLogo />` usages (AuthPanel, MainLayout app bar,
Dashboard loaders + firstrun), the `.budgie-logo`→`.tandem-logo` CSS selectors, `favicon.svg`, `<title>`, brand text
("Budgiely"→"TandemTab"), and the "Welcome to…" Loc key. **Logo enlarged** (app bar 26→38px, sign-in 44→64px). The budgie
mascot is fully retired (the pun belonged to the old name). README/MAUI host title still say Budgiely — non-user-facing, update if you like.

### Session 11l — family-friendly visual refresh (mint + cream, Quicksand, mint logo, new tagline). UI-only.
- **Palette → mint/cream.** Swept the whole indigo family → mint across all scoped CSS (PowerShell map: `#4f46e5→#13a06e`,
  `#4338ca→#0e7c55`, `#eef0fb→#e4f6ee`, + ~10 tints + the `rgba(79,70,229,…)` shadows). Red/amber/green semantics kept;
  savings/success greens were already green so it's cohesive. Page background warmed to cream (`body{background:#fbf7ef}` in
  `app.css`). **To recolor again, re-run the same map** — colors are still hardcoded hex, not CSS variables (worthwhile future cleanup).
- **Font → Quicksand** (Google Fonts link in `index.html`; `font-family` set in scoped CSS + `app.css`). Numbers kept legible
  via `font-variant-numeric: tabular-nums` on `.dash` (honest fix — Quicksand's geometric digits scan poorly otherwise).
- **Logo recolored** to a mint gradient (`BudgieLogo.razor`). **Tagline** → "Track together, save together." + hint
  "Simple family goals, zero stress." (`AuthPanel`, `<title>`); BG translations added.
- **Cache:** bumped `app.css?v=3` (not fingerprinted — bump on every global-CSS change).
- **NOT done (flagged): per-member pastel accent colors** — a real feature (store a color on `AccountMember` →
  serializer/EF, then paint contributions/avatars), not a CSS tweak; natural next step. No App Store listing exists yet
  (web on Cloud Run; MAUI unpublished) — only the in-app tagline/title were updated.

## Session 10 (2026-06-25) — branding, polish, data import, perf
All on `main`, deployed (latest revision ~finapp-00021). Highlights since the 06-24 debt cleanup:
- **Rebrand → Budgiely:** `BudgieLogo.razor` (SVG budgie with a €-coin belly) in the app bar + sign-in screen;
  name/title/`<title>`/README/first-run all say Budgiely; SVG `favicon.svg`; tagline "Budget like a budgie." (EN/BG).
  **Empty-state mascot** (bobbing budgie on first-run, respects `prefers-reduced-motion`) + **bird-themed microcopy**
  on the empty states + overspend banner.
- **Fancier invitations panel:** `InvitationsPanel.razor.css` (it had **no** scoped CSS before, so it rendered
  unstyled — its `.panel`/`.list` belonged to Dashboard's scope). Framed gradient card, avatars, gradient Accept.
- **Modal centering fix (important gotcha):** the app loads **Bootstrap**, whose `.modal`/`.modal-backdrop`
  collide with ours; Bootstrap leaked `position:fixed;top;left;height:100%` onto our box. Fixed by overriding on
  scoped `.modal` — but **the scoped-CSS minifier strips declarations whose value is the CSS default**, so the
  first try (`position:static`/`height:auto`) vanished from the published bundle. Final fix uses **non-default**
  `position:relative; height:fit-content`. (If you ever override a leaked default again, use a non-default value.)
- **Profile / change password:** click the username in the app bar → modal. New `POST /auth/password` (authorized)
  + `AuthService.ChangePasswordAsync` + client `ChangePasswordAsync`.
- **Account-switch cache:** `BudgetingState` caches the deserialized aggregate per account (instant switching, no
  re-fetch); subscribes to **all** accounts so `AccountChanged` invalidates; only trusted while `sync.IsConnected`;
  reconnect clears it. `SyncClient` gained `IsConnected` + `Reconnected`.
- **Responsive pass** (header stacks, tabs scroll, cards reflow, forms wrap, budget tree grid drops the bar on
  phones). **Budgets tab** = aligned CSS grid (name | ratio | 🧾 | bar | %). **Expenses tab** = big "Add expense"
  button → modal; **opens by default on phones** (JS `finappViewportWidth`). **Tighter, capped (90vh) modals.**
- **Secrets → GCP Secret Manager:** `ConnectionStrings__FinApp` (secret `finapp-db`) and `Jwt__Key` (secret
  `finapp-jwt`) — both rotated, plaintext env vars removed, old versions disabled. To change one secret on the
  service use `--update-secrets` (NOT `--set-secrets`, which replaces the whole list).

## Data import tool — `tools/FinApp.Seed`
Console seeder: logs in, **creates a NEW account (deletes a same-named one first — idempotent)**, builds the
aggregate via the domain + `AccountSnapshotSerializer`, pushes the snapshot. Two modes:
- CSV expenses (`SEED_CSV`, `sample-expenses.csv` documents the layout).
- **Family workbook** (`SEED_FAMILY=family.json`): `extract_family.py` parses the user's monthly budget xlsx
  (Jan–Jun) → `family.json` (single fund = sum of the top fund rows; income via the running-sum total under
  "Приход" mapped to January's contributor template; budgets/savings/expenses; expense dates recovered from
  Excel's mis-parsed dd/mm). The seeder closes every period except the latest. **`family.json` + workbook dumps are
  gitignored** (private financial data). Run against local first; the user ran it live into their own "Family" account.
- Bundled Python lives at the Cloud SDK path (no `python` on PATH); install openpyxl into it.



## Tech-debt cleanup (2026-06-24, on `main`)
`feature/account-tab-changes` was **merged + pushed to `origin/main`** (GitHub shonzi91/FinApp). Debt status:
- ✅ **Deploy cache-busting:** `FinApp.Server` now serves the hash-less entry files
  (`FinApp.App.Web.styles.css`, `index.html`, SPA fallback) with `Cache-Control: no-cache, must-revalidate`,
  so a new deploy is picked up without a manual hard-refresh (fingerprinted `_framework`/`_content` stay cached).
- ✅ **Localization:** all 43 modal action buttons + 21 modal titles wrapped in `@Loc[...]` with BG strings.
  Remaining EN-only tail = deep modal hints/labels + some `title=` tooltips (smaller follow-up).
- ✅ **Neon password rotated + moved to Secret Manager.** The user reset the Neon role password. The connection
  string now lives in **GCP Secret Manager** secret `finapp-db` (project `finapp-1111`); Cloud Run reads it via
  `--set-secrets=ConnectionStrings__FinApp=finapp-db:latest` and the **plaintext env var was removed**. The runtime
  SA `85638328674-compute@developer.gserviceaccount.com` has `secretAccessor`. Old secret version 1 (leaked value)
  is **disabled**. Live on **finapp-00013** (startup `EnsureCreated()` succeeded → DB auth OK).
  - To rotate again: add a new secret version (`gcloud secrets versions add finapp-db --data-file=- --project
    finapp-1111`), then `gcloud run services update finapp --region europe-west1 --set-secrets=
    ConnectionStrings__FinApp=finapp-db:latest`. `gcloud run deploy --source .` keeps the secret binding (reuses config).
- ✅ **`Jwt__Key` rotated + moved to Secret Manager** (secret `finapp-jwt`, fresh 48-byte random key). Live on
  **finapp-00016**. Only `Database__Provider` remains a plain env var; both `ConnectionStrings__FinApp` and
  `Jwt__Key` are secret-backed. Rotating the key invalidated existing JWTs (everyone re-logs in).
  - ⚠️ **gcloud gotcha:** `--set-secrets` **replaces the entire** secret-env list — passing one key drops the others
    (this briefly broke the DB binding). Use `--update-secrets=KEY=secret:latest` to change one, or `--set-secrets`
    with **all** keys listed. Current full form:
    `--set-secrets="ConnectionStrings__FinApp=finapp-db:latest,Jwt__Key=finapp-jwt:latest"`.
NOTE on working style (see memory): this user prefers I **proceed with sensible defaults rather than ask** — don't gate work behind clarifying questions; state assumptions and move.

## Session 9 (2026-06-24) — Account-tab cleanup (branch `feature/account-tab-changes`, commit 6397a29)
Four UI changes (no domain math change; 77 domain tests still pass — domain test count is 77 now, not 74):
1. **Removed the "contributed but not allocated" deposit gate** — `State.HasUnallocatedFunds`/`Unallocated`
   deleted; the warn hint + deposit-button disable are gone. Deposits are never blocked now.
2. **Savings panels renamed:** the move-to-budget/bucket panel "Spend savings" → **"Budget savings"**;
   the real-expense panel "Spend as expense" → **"Spend savings"**. BG translations updated (Localizer:
   `Budget savings`=Бюджетирай спестявания, `Spend savings`=Похарчи спестявания).
3. **"Contributed" card → "Current"** (label flips to **"Closed on"** when the period is inactive). Value =
   **`State.ClosingBalance`** (`Period.ExpectedClosingBalance` = the money actually in the account: opening +
   deposits − expenses − external-out). While active that's the live "Current" balance; once closed it's exactly
   what the period "Closed on". Period status badge **"Open" → "Active"**. **Removed the header "Closing" balance**
   (the card now carries it). NOTE: the savings **available-to-save** ceiling is **deliberately left on the
   contributed/allocatable pool** (`MaxAdditionalSavings`, hint "contributed − budgeted"), *not* the closing
   balance — savings is planned from contributions, and the closing balance includes opening fund money you may
   need. If the user later wants savings capped by total balance, that's a deeper domain change.
4. **Removed the "Recent expenses" section** (expenses live on the Expenses tab, grouped by date).
   `BudgetingState.RecentExpenses` deleted.
**Money model redesign (2026-06-24, domain change; 73 domain + 5 persistence + 19 server pass):**
Re-based allocation on **the money you actually have** and dropped the signed-carryover machinery.
- **`AvailableToSave = ExpectedClosingBalance − BudgetedTotal`** (was `Allocatable − BudgetedTotal`). So
  `MaxAdditionalSavings = max(0, money-in-account − budgeted − saved)`. **Opening fund balances now count** toward
  what you can save/budget — carried-over money simply sits in the openings, so it's spendable with no separate
  mechanism. The **budget cap** moved to the same basis: `budgeted + saved ≤ ExpectedClosingBalance`.
- **Carryover is now positive-only / implicit.** Removed `Period.Allocatable`, `SetCarryover`, `CarryoverTotal`,
  `UnallocatedShortfall`, `CoverCarryoverFromSavings`, and the `CarryoverSource` branches in
  `Remove/EditSavingMovement`. `Period.CarriedIn` is now **vestigial** (kept as an always-zero field +
  EF column + serializer field purely for back-compat — no migration). `CarryoverSource` const kept so legacy
  snapshots still deserialize. `StartNextPeriod` no longer calls `SetCarryover` — it just sets the real opening
  balances. UI: removed the "From previous period" row, the inline "Cover shortfall" form, the shortfall optgroup
  in Budget-savings, and `BudgetingState.{UnallocatedShortfall,HasUnallocatedShortfall,CarryoverCategoryId,
  CoverCarryoverFromSavings,CarryoverThisPeriod}`.
- Tests: deleted the 4 obsolete carryover/shortfall tests, inverted `Opening_funds_*` to assert openings count,
  rewrote the carryover test as `Opening_balances_carry_over_and_are_fully_allocatable`. (Domain 77 → 73.)
- ⚠️ **Known caveat (told the user):** because the cap base subtracts expenses, editing a budget *after* spending
  against it can be limited (the spent money lowers the ceiling). Acceptable for now; revisit if it bites.
- **Follow-up (2026-06-24, after user saw a confusing over-committed period): transfer guard + deficit annotation.**
  Expenses stay uncapped (overspending allowed), but a *discretionary* transfer-out can no longer break the
  savings earmark: `Period.TransferOut` throws if `amount > AvailableToTransferOut`
  (= `ExpectedClosingBalance − max(0, SavingsNetTotal)`). New `Period.AvailableToTransferOut` +
  `BudgetingState.{AvailableToTransferOut, HasDeficit}`. UI: the Transfer-money form caps/​disables sends to
  another account at the unreserved cash and shows "Available to send: X"; the **Saved this period** card shows
  "€X not backed by cash" (the Deficit) instead of the savings % when underwater. Test:
  `Transfer_out_cannot_break_the_savings_earmark`. 74 domain tests pass.

**Item 5 — Account-tab simplification (built; UI-only, no domain change; 77 domain tests + Web build green):**
- **Unified "Transfer money" panel** replaces the always-on inline fund-transfer form **and** the 📤 send-to-account
  modal. One `From [fund] → To [fund | other account]` picker (grouped `<optgroup>`s) + amount + note; `DoTransfer()`
  routes to `TransferFunds` (fund dest) or `TransferToAccount` (account dest). Removed `Modal.TransferOut` + its
  handlers/fields (`OpenTransferOut`/`ConfirmTransferOut`/`_extFromFundId`/`_extToAccountId`/`_extAmount`/`_extNote`).
- **One merged transfers ledger** (`MergedTransfers()` in Dashboard `@code`): fund transfers + external transfers,
  newest-first, in a single list (fund rows get edit+delete, external rows delete-only). Replaces the two separate logs.
  The **Funds panel is now just a balance sheet.** NOTE: Razor gotcha — at a `@switch`/`case` top level the body is C#
  *code* context, so a bare `var transfers = MergedTransfers();` inside the `@if {}` is correct; `@{ }` there is a
  RZ1010 error (only valid inside markup, e.g. nested in a `<section>`).
- **Inline "Cover shortfall"** on the carryover row: when a negative leftover leaves an `UnallocatedShortfall`, a
  bucket-select + amount + "Cover shortfall" button (`CoverShortfall()` → `CoverCarryoverFromSavings`) sits right there
  instead of pointing the user to the Savings tab.
- **Simplified deposit:** the contribution form is now **amount + date + Deposit** by default; category/fund selects +
  category management are behind a `⋯` toggle (`_depShowDetails`), defaults pre-filled.
- New Localizer keys (EN=BG): Transfer money, Other accounts, Cover shortfall, Category & fund, + the transfer hint.
- **Not built (flagged for a separate decision): item 5E** — the "informational-only" sub-funds (which drive the
  `SubFundsMismatch` hint + `InitialBalance.Informative` flag) are a half-real concept; make them real (parent = Σ
  children) or drop them. That's a domain commitment, left for the user to choose.

## Session 8 (2026-06-22) — deployed live + i18n + UX + Expenses features
**LIVE at https://finapp-85638328674.europe-west1.run.app** (Google Cloud Run, project `finapp-1111`, region
europe-west1; free **Neon Postgres**, eu-central-1). Redeploy: `gcloud run deploy finapp --source . --region europe-west1`
(reuses env vars). Latest revision finapp-00006. **⚠️ Neon DB password was exposed in a log read during debugging — rotate it.**
- **Deploy model:** one-origin container (`FinApp.Server` hosts API + SignalR + WASM). Cloud Build builds the Dockerfile
  (`gcloud run deploy --source .`) — no local Docker needed. DB provider switch: SQLite (dev/tests/MAUI) vs Postgres
  (`Database__Provider=Postgres` + `ConnectionStrings__FinApp`, accepts a `postgres://` URI; `EnsureCreated()`). `--max-instances 1`
  (SignalR has no backplane). Also: `fly.toml`, `deploy/oracle/`, `deploy/cloudrun/`, GHCR CI (`.github/workflows`). Dockerfile
  installs python + `<WasmBuildNative>false</WasmBuildNative>` (Emscripten relink was failing/slow in CI).
- **EN/BG localization:** `Localizer` service (English text = key, BG dictionary, persisted to localStorage; `Loc.T("…")`/
  `Loc["…"]`). Registered in both hosts. 🇬🇧/🇧🇬 flag switcher (top bar when signed in; inside the login card when signed out).
  Components using Loc **must subscribe to `Loc.Changed`** (parameterless children don't re-render on parent render).
  Localized MainLayout, AuthPanel, first-run, and the main Dashboard chrome. **Remaining EN-only:** deep modal bodies +
  icon `title` tooltips + BudgetTreeNode — same `@Loc["…"]` mechanism, just not yet wrapped.
- **UX polish:** `Dashboard.Run()` guards double-submits + shows a "Saving…" pill + dims the dash + maps errors
  (409/401/network) to human text; dismissable error banners. Login screen restyled (`AuthPanel.razor.css`, segmented tabs,
  placeholders/autocomplete). Date inputs styled (incl. modals). Sign-out button restyled. Period label → `(n/n)`. **Top
  app-bar hidden on the login/signup screen.**
- **Expenses features (live):** #3 Expenses-tab **day view** (`_dayView` DateOnly?; period-bounded date picker + ◀/▶ +
  "All days"; new expenses default to that day). #4 "All expenses" **grouped by date with clickable separators** → open day
  view. #5 **collapsible** expense list under each budget category (`BudgetTreeNode._expanded`). #6 Savings-tab **"Spend as
  expense"** panel (reuses `BudgetingState.SpendFromSavings`).

## #1 + #2 — DONE (2026-06-22, live as revision finapp-00007); 101 tests pass
Contributions reshape — built & deployed. (Server stores the body as opaque JSON, so no Postgres migration was needed;
SQLite migration `AddContributionCategoriesAndFundAttribution` added for MAUI. Serializer round-trips new fields with
back-compat defaults.) **Implemented:** `ContributionCategory` (account-level, Add/Rename/Remove, dup guard, remove blocked
when referenced; new accounts seed Salary+Other); `Contribution` itemized `(MemberId, CategoryId, FundId, Date, Paid)`,
deposits merge by (member,category,fund); `Period.Deposit/EditContribution/RemoveContribution` (by id); `FundBalance`
includes attributed deposits (fund balances now sum to expected closing); own-only edit (`CanHandleContribution`).
Contributions UI = deposit form (category+fund+amount+date) + category chips + itemized own-editable list.
Original design notes (kept for reference):
- **#1 Contribution categories per account** (e.g. Salary/Rent/Insurance/Vouchers): new account-level `ContributionCategory`
  entity + Account Add/Rename/Remove (dup-name check via `NameEquals`; block remove if in use). The "From previous period"
  leftover is NOT a contribution (it's `Period.CarriedIn`) — it keeps its own pseudo-category, unaffected.
- **#2 Fund-attributed deposits** (CONFIRMED: a deposit **increases the chosen fund's balance**). `Contribution` becomes an
  **itemized entry** `(Id, MemberId, CategoryId, FundId, Paid, Date)` — **multiple per member** (default chosen; user didn't
  object). `Period.Deposit` adds an entry (no longer merges per member); edit/remove become **by contribution Id**.
  `FundBalance` adds `+ Σ deposits where FundId==fund` (so fund balances now sum to `ExpectedClosingBalance`). Permission:
  **a user may only add/edit/remove their OWN contributions** (`MemberId == CurrentMemberId`) — enforce in BudgetingState/UI.
- **Ripples to handle:** `BudgetingState.RecordDeposit/EditDeposit/RemoveDeposit` (now by id + take category+fund);
  `TransferToAccount`'s cross-account `Deposit` needs a category+fund (use a default/"uncategorized" + default fund);
  serializer `ContributionNode` (+CategoryId/FundId/Date) and new `ContributionCategoryNode` + `AccountNode`; `FinAppDbContext`
  mapping (+ContributionCategories table, Contribution columns) + SQLite migration; Dashboard Contributions panel becomes an
  itemized list + category management UI + deposit form with category+fund selects (own-only editable); Localizer strings; tests.

---

(Earlier sessions below.)


> **Resuming 2026-06-18+:** EF migrations **and** the full multi-user sync feature (auth, accounts, invitations,
> SignalR live sync, full-aggregate snapshot data sync) are **done & verified**. Several rounds of **budgeting changes**
> have since landed from live testing — see "Post-M3 budgeting changes" and "Session 2/3/4/5" below.
> **98 tests pass** (74 domain + 5 persistence + 19 server). **Run `FinApp.Server` before the MAUI app.**
> Latest (**Session 5**): the "From previous period" leftover is **this period's opening total − the previous period's
> closing balance**, held **signed in `Period.CarriedIn`** (not clamped — a negative shortfall reduces what's allocatable),
> carried in as allocatable money (opening balances are not themselves allocatable); plus a nested expense category
> dropdown, a savings-movements edit/undo list, opening+closing balance cards, period-ops icon buttons, cross-account fund
> transfers (→ contribution), and icon-only buttons with tooltips.

## What this is
Privacy-first budgeting/expense tracker, **period by period**, inside first-level accounts (Personal/Shared/Family). Local-first: data stored encrypted on device; an optional server (not built yet) only relays end-to-end-encrypted change events for multi-user sync.

**Location:** `C:\Projects\FinApp` (separate from the session's default `C:\Projects\Global.Data.Api`). .NET 9.

## Stack & key decisions
- **UI:** Blazor, shared across MAUI Blazor Hybrid (mobile/desktop) + Blazor WASM (web, not built yet). Native-first for the strong privacy story.
- **Storage:** SQLite + EF Core, **SQLCipher-encrypted**. Key in OS keystore (MAUI `SecureStorage`, DPAPI on Windows), file-key fallback.
- **Sync (planned):** ASP.NET Core + SignalR relaying E2E-encrypted event blobs. (MQTT/RabbitMQ only if scale demands.)
- **Domain:** rich, immutable entities; append-only expense ledger; categories/savings/**funds** stored **flat with `ParentId`** (tree computed) and referenced by `Guid` so they round-trip through the relational store.

## Terminology (since 2026-06-17)
- **Domain account** = the `Account` aggregate (funds/periods/budgets/expenses/savings). Has an **owner** (creator) and **contributors**.
- **User account** = a person who signs in (username/email/password) — the `User` entity. A user owns and contributes to domain accounts.
- **Contributor = member**: a contributor is an `AccountMember` whose `UserId` is the real `User.Id`. Owner-only: rename/delete the account. Any contributor: edit everything inside + invite others.

## Solution structure
```
src/FinApp.Domain/         pure C# model + rules + domain services (no UI/storage)
  Accounts/   Account (root: OwnerUserId + members/contributors, categories, savings, funds, periods), AccountMember
  Users/      User (username/email/PasswordHash)
  Sharing/    Invitation (Pending/Accepted/Declined state machine)
  Budgeting/  Category, Budget, Expense
  Funds/      Fund (account-level, replaces old FundType enum), FundTransfer
  Periods/    Period (budgets/expenses/contributions/savings/initial-balances/fund-transfers), InitialBalance, Contribution
  Savings/    SavingCategory (goal: GoalAmount/AlertThreshold/NotifyOnMilestone), SavingAllocation (SourceExpenseId link)
  Common/     Entity, Money, IPasswordHasher
  Services/   BudgetCoverage, SavingsReport, Carryover, Reconciliation
src/FinApp.Persistence/    EF Core + SQLite/SQLCipher; FinAppDbContext (Accounts+Users+Invitations), AccountStore (Migrate()), Migrations/
src/FinApp.Contracts/      DTOs shared client<->server: Auth, Accounts (+AccountSnapshot), Invitations, Sync events
src/FinApp.Server/         ASP.NET Core minimal API + SignalR. Auth (PBKDF2 + JWT), Accounts, Invitations, Sync/SyncHub + SyncNotifier
src/FinApp.Shared.UI/      shared Blazor: Services/BudgetingState.cs, Pages/Dashboard.razor (4 tabs), Components/BudgetTreeNode.razor
src/FinApp.App.Maui/       MAUI Blazor Hybrid host (Windows target only for now); MauiDatabaseSettings = DB path + SQLCipher key
tests/FinApp.Domain.Tests/        66 tests (incl. FundsTests, SavingsTests, MoneyEnvelopeTests, UsersAndSharingTests)
tests/FinApp.Persistence.Tests/   5 tests (encrypted round-trip + wrong-key + snapshot serializer)
tests/FinApp.Server.Tests/        19 tests (auth, accounts authz, invitations, SignalR live push) via WebApplicationFactory
```

## Multi-user / server (M0–M3 COMPLETE & verified, 2026-06-17)
Posture: server is **source of truth for shared accounts** and relays live changes (plaintext at rest for now;
`AccountSnapshot.Payload` is a single opaque blob so it can become an E2E ciphertext later). Auth = custom
**User + PBKDF2 + JWT bearer** (not ASP.NET Identity). End-to-end verified via curl (register→create→invite→accept→
snapshot round-trip) and the MAUI app launches against the live server.
- **API:** `POST /auth/register|login`, `GET /me`; `GET/POST /accounts`, `PUT /accounts/{id}/name` + `DELETE` (owner-only);
  `GET/PUT /accounts/{id}/snapshot` (any contributor; optimistic concurrency on `Version`);
  `POST /accounts/{id}/invitations` (any contributor), `GET /invitations/pending`, `POST /invitations/{id}/accept|decline`.
- **SignalR** `/hubs/sync`: per-user group (invitations) + per-account group (change relays). Token via `?access_token=`.
  Clients `Subscribe(accountId)` (awaited) after accepting an invite to avoid the OnConnectedAsync join race.
- **Account data sync:** `AccountSnapshotSerializer` (in Persistence) serializes the **full aggregate to JSON with
  id preservation** (reflection helper restores ids/closed-status/collections). Server stores it as an opaque blob row
  (`AccountSnapshotRow`, keyed by account) — never parsed server-side. Client: header (name/owner/members) is
  server-authoritative; body (funds/categories/savings/periods) travels in the snapshot. New account → client seeds the
  starter body on first open and PUTs v1.
- **Client (Shared.UI/Services):** `FinAppApiClient` (typed HttpClient), `AuthState` (token in `ITokenStore` →
  MAUI `MauiTokenStore`/SecureStorage), `SyncClient` (SignalR). `BudgetingState` reworked: loads summaries + snapshot
  from the server, edits the in-memory aggregate, and **every `SaveAsync` pushes the snapshot**; attributes actions to
  the signed-in user (`auth.UserId`); applies live `AccountChanged`/`InvitationReceived`. UI: `AuthPanel` (sign-in/up),
  `InvitationsPanel` (accept/decline), `MainLayout` auth-gate + sign-out, Dashboard owner-only rename/delete + 👥 invite.
- **Server DB:** plain SQLite `finapp-server.db` (unencrypted; reuses `FinAppDbContext` mapping via `BuildOptions(path, null)`),
  migrated on startup. JWT signing key in `appsettings.json` `Jwt:Key` (dev-only placeholder — replace for prod). Server
  listens on `http://localhost:5179` (`Urls` in appsettings); the MAUI client points at the same URL (`MauiProgram.cs`).
  **Run the server before the app.**

## Post-M3 budgeting changes (2026-06-18, from live testing)
- **Period removal:** `Account.RemoveLatestPeriod()` (+ `Period.Reopen()`) deletes the latest period and re-activates the
  previous one. Latest-only so the chain stays contiguous. UI: 🗑 remove-period button next to "Start next month" (shown
  when >1 period) → `Modal.RemovePeriod`. `BudgetingState.RemoveLatestPeriod()`.
- **Money model / "Available" envelope:** `Period.Available = InitialTotal + ContributionsPaidTotal + CarriedIn`
  (opening fund balances + contributions; **does not shrink as you spend**). New cap: **budgeted + saved ≤ Available**,
  enforced by `Period.SetBudget(...)` (the UI path; `AddBudget` stays **uncapped** for savings-conversion / copy-forward).
  New: `Period.Unplanned` (envelope not yet budgeted/saved → rolls forward), `MaxAdditionalBudget`, and `Deficit`
  (= savings earmark beyond actual cash left). **Expenses may overspend** (not capped) → surfaces as `Deficit`.
  UI: **Available** card next to Contributed (with "X to allocate") + an "Overspent by X" banner when `Deficit > 0`.
- **Savings bucket→bucket transfer:** `Period.TransferSavings(from, to, amount, date)` — net-neutral, **not** capped.
  `BudgetingState.MoveSavingToBucket(...)`; Savings tab now has a 3rd path "Move to bucket" beside Move-to-budget / Spend-now.
- **UI cleanups:** removed the Budgets-tab **dates/reschedule** control (and `_editingDates`/`Reschedule` code — note
  `Account.ReschedulePeriod` / `BudgetingState.ReschedulePeriod` still exist, just unused by the UI). Removed the contribution
  **pledge step + due-date picker**: deposits now stand alone — `BudgetingState.RecordDeposit` auto-creates a zero-pledge
  `Contribution` on first deposit; each member row is just **amount + Deposit**, display reads "X deposited" / "no deposits yet".
- **Tests:** added `MoneyEnvelopeTests` (later rewritten in Session 2 for the contributions-based model). Totals at this
  point were **79**; now **84** after Session 2 — see below.
- **Heads-up:** the "deposit blocked while funds are unallocated" rule (`State.HasUnallocatedFunds`,
  = `MaxAdditionalSavings > 0`) fires whenever contributed money isn't yet budgeted/saved. Still in place; revisit if
  it feels too aggressive.

## Session 2 budgeting changes (2026-06-18, second round from live testing)
Six items, all shipped & green (84 tests). The big one reverses the post-M3 "Available envelope":
- **Expense date picker:** add-expense form + both expense modals take a `Date` (defaults to today). `BudgetingState.AddExpense`/
  `EditExpense` and `Period.EditExpense` now thread a `DateOnly`.
- **Edit/delete a deposit:** `Contribution.SetPaid`, `Period.SetDeposit`/`RemoveDeposit` (RemoveDeposit drops the contribution
  when nothing was pledged, else zeroes Paid). `BudgetingState.EditDeposit`/`RemoveDeposit`; ✏️/🗑️ on each member's
  "X deposited" row → `Modal.EditDeposit`/`DeleteDeposit`.
- **Money model is now contributions-based (the "Available" card/concept is gone).** `Period.Allocatable = ContributionsPaidTotal
  + CarriedIn` (opening fund balances **excluded** — they're just where money sits). Budgets + savings caps and
  `AvailableToSave`/`MaxAdditionalSavings` all key off `Allocatable`. Removed `Period.Available`/`Unplanned`/`MaxAdditionalBudget`
  and `BudgetingState.Available`/`Unplanned`. `Deficit`/overspend banner kept (independent of the envelope basis).
- **Savings bucket initial balance:** `SavingCategory.InitialAmount` (set via `Account.SetSavingInitialAmount`), editable only
  during initial setup (`State.CanSetInitialSavings == PeriodCount == 1`). It counts toward the bucket's **balance & goal**
  but is **excluded from the savings rate** — `SavingsReportService` split into `AllocatedTotal` (rate numerator, allocations
  only) vs `AccumulatedTotal` (display, + initial). Fixes the "huge savings %" bug when seeding a large starting balance.
- **Spend savings unified:** one source bucket → one destination `<select>` grouped by `<optgroup>` (Budgets = all categories,
  Savings buckets = the others) + a single **Move** button (`Dashboard.MoveSaving` dispatches to `ConvertSavingToBudget` or
  `MoveSavingToBucket`). The old "Move to budget / Spend now / Move to bucket" trio is gone (Spend-now dropped per the user;
  `BudgetingState.SpendFromSavings` still exists if it's ever wanted back).
- **Informational sub-funds:** `Fund.ParentId` (one level deep). Funds render as a tree (root funds with balances + a ➕ to add
  a child; children are indented labels with **no balance** — all money/calc stays on the parent). Money pickers (expense/
  transfer/opening) list `State.RootFunds` only. `FundRemovalBlocker` returns "it has sub-funds" for a parent with children.
- **Header:** "Hello, {user}" + Sign out right-aligned (`.appbar-user { margin-left:auto }` in `MainLayout.razor.css`).
- **Migration:** `20260618083933_AddSavingInitialAmountAndSubFunds` (SavingCategories.InitialAmount + Funds.ParentId). Gotcha:
  `Account.RootFunds` (IEnumerable<Fund>) had to be `Ignore`d in `FinAppDbContext` like `RootCategories`, or EF scaffolds a
  bogus `AccountId1` shadow FK. Snapshot serializer extended (`FundNode.ParentId`, `SavingCategoryNode.InitialAmount`) —
  missing-in-old-JSON → defaults, so existing snapshots upgrade cleanly.

## Session 3 budgeting changes (2026-06-18, third round)
- **Fund removal + optional balance transfer:** `Account.RemoveFund(fundId, moveOpeningBalancesTo)` + `Period.MoveInitialBalance`
  (total-preserving). Opening balance is not a hard `FundRemovalBlocker` (expenses/transfers/sub-funds still are; only-fund still
  blocks). **Updated 2026-06-19:** removal is **always allowed** — transfer is opt-in. The Delete-fund modal shows a "Move balance
  to" dropdown with a "— don't move —" default (only when `FundHasOpeningBalance`, which ignores zero amounts); passing no target
  just drops the balance.
- **Fund transfers are editable/removable:** ✏️/🗑️ on each transfer-log row → `Modal.EditTransfer`/`DeleteTransfer`.
  `Period.EditFundTransfer(id, from, to, amount, note)` (remove + re-add, keeps the original date) and `RemoveFundTransfer(id)`;
  `BudgetingState.EditFundTransfer/RemoveFundTransfer/FindFundTransfer`. No schema change. Tests: **72 domain + 5 + 19 = 96**.
- **Edit/remove savings deposits:** the "Add to savings" panel now lists this period's manual deposits with ✏️/🗑️.
  `Period.ManualSavingDeposits()` (positive, un-noted, unlinked allocations), `EditSavingDeposit` (remove+re-add, re-checks
  the cap, keeps the date), `RemoveSavingAllocation`. `BudgetingState.SavingDepositsThisPeriod`/`EditSavingDeposit`/
  `RemoveSavingDeposit`; modals `EditSavingDeposit`/`DeleteSavingDeposit`.
- **Pledges removed — direct deposits only.** `Contribution` is now just `MemberId` + `Paid`. `Period.Deposit(memberId, amount)`
  replaces `SetContributionPledge`+`RecordContributionPayment` (creates the row on first deposit, adds after). Dropped
  `Pledged`/`DueDate`/`Outstanding`/`IsFullyPaid`/`IsOverdue`/`OutstandingContributions`/`ContributionsPledgedTotal` and the
  **"Deposits pending" alert**. Migration `20260618125342_DropContributionPledge` drops the two columns (EF SQLite table-rebuild;
  the PRAGMA-in-transaction warning is benign). Serializer `ContributionNode` is now `(Id, MemberId, Paid)` — old snapshots
  read fine (extra Pledged/DueDate JSON ignored).
- Tests: **66 domain + 5 persistence + 19 server = 90**.

## Session 4 budgeting changes (2026-06-19, fourth round)
- **No duplicate names** (case-insensitive, per account): `Account.AddCategory/RenameCategory`, `AddSavingCategory/RenameSavingCategory`,
  `AddFund/RenameFund` reject dupes via a private `NameEquals`; `BudgetingState.AddAccount/RenameAccount` check the user's
  account summaries. (Per type — a category and a fund may share a name.)
- **Sub-funds can hold an informative initial value:** `InitialBalance.Informative` flag (migration `AddInformativeInitialBalance`).
  `Period.SetInitialBalance(fundId, amount, informative)`, `InitialTotal` excludes informative, `OpeningBalanceOf`, `RemoveInitialBalance`.
  `BudgetingState.SetFundOpeningBalance` marks a sub-fund's value informative automatically; `SubFundOpeningTotal`/`SubFundsMismatch`
  drive a soft "doesn't match the parent" hint (never blocks). Funds panel shows each sub-fund's value; Add/Edit-fund modals expose it.
  Fund removal purges a sub-fund's informative rows (`Account.FundHasOpeningBalance` now counts real balances only).
- **Item 5 (budget cap) was already satisfied** by the shared-pool rule (`budgeted + saved ≤ contributed`, conversion bypasses) —
  no code change, added a test (`Saving_conversion_can_push_a_budget_past_contributions`).
- **Savings totals moved:** Account-tab card is now **"Saved this period" + % of contributions** (was "Savings (total)");
  the Savings tab shows **Total saved** alongside the period/all-time rates.
- Tests: **69 domain + 5 persistence + 19 server = 93**.

## Carryover redesign (items 3+4, DONE 2026-06-19)
Replaced the interactive "Carry over previous leftover" row + `CarryoverService` allocation flow.
- **On "Start next month"** the modal now lists each top-level fund with its **real current balance** (pre-filled from the
  previous `FundBalance`, editable). `BudgetingState.StartNextPeriod(copyBudgets, realFundOpenings)` sets those as the new
  period's opening balances and computes the carryover.
- **"From previous period" carryover = `prevContributed − prevSaved − prevSpent − shortfall`**, where
  `shortfall = prev.ExpectedClosingBalance − newRealOpeningTotal`. Stored as a `Contribution` with sentinel member
  `Period.CarryoverSource` (clamped ≥ 0), shown as a read-only "From previous period" row in Contributions. Round-trips on the
  existing `Contribution` serialization — **no migration**.
- It feeds `ContributionsPaidTotal`/`Allocatable` (budget/save against it) but is **excluded from `ExpectedClosingBalance`**
  (`= InitialTotal + (ContributionsPaidTotal − CarryoverTotal) − ExpensesTotal`) to avoid double-counting the carried money.
- The **reconciliation alert** and `State.Reconciliation` were removed (superseded by the real-value entry). `CarriedIn`,
  `Period.CarryToSavings/CarryToBudget`, `CarryoverService` + `CarryoverTests` are now **vestigial** (kept, always 0, so no
  migration); `BudgetingState`'s carry methods/`PeriodReconciliationService` field were removed. `State.BudgetedCategories` is
  now unused.
- Tests: **71 domain + 5 persistence + 19 server = 95** (added carryover allocatable/closing + clamp tests).

## Session 5 budgeting changes (2026-06-19, fifth round) — 7 items
All shipped & green (**98 tests**: 74 domain + 5 persistence + 19 server). Migration `AddExternalTransfersAndSavingMovementLinks`.
1. **Nested expense category dropdown:** `BudgetingState.CategoryOptions` returns categories in tree order with depth;
   the Expenses add-form, the Edit-expense modal and the Spend-savings "to a budget" list render them indented
   (`Dashboard.IndentLabel`, "↳" prefix). Flat `<select>`, so it round-trips fine.
2. **Savings-movements list (edit/undo):** "spend savings" moves are now reviewable. `SavingAllocation` gained
   `BudgetCategoryId` (set on move-to-budget) and `TransferPairId` (links the two halves of a bucket→bucket transfer).
   `Period.SavingMovements()` lists the to-budget drawdowns + the outgoing half of bucket transfers;
   `RemoveSavingMovement` reverses the budget bump / drops both transfer halves; `EditSavingMovement` = remove + re-apply.
   `BudgetingState.SavingMovementsThisPeriod`/`SavingMovementTarget`/`Edit…`/`Remove…`; modals under the Savings tab's
   "Spend savings" panel.
3. **Opening + Closing balance cards:** `BudgetingState.OpeningBalance` (= `Period.InitialTotal`, the real opening fund
   sum; unaffected by allocations) and `ClosingBalance` are shown side-by-side in the header **for every period, open or
   closed** (the old latest-period-only inline closing line is gone).
4. **Period dates editable + period ops are icon buttons:** the period row next to the dates now has 📅 edit-dates
   (`Modal.EditPeriod` → `State.ReschedulePeriod`), 🗑️ remove-period, ⏭️ start-next-month — pulling those controls out of
   the balance area so both read cleaner.
5. **Carryover = this opening − previous closing, signed.** `Period.Allocatable` stays `ContributionsPaidTotal + CarriedIn`
   (opening balances are **not** directly allocatable). The "From previous period" leftover set in
   `BudgetingState.StartNextPeriod` is `realOpeningTotal − previous.ExpectedClosingBalance`, stored **signed and unclamped**
   in `Period.CarriedIn` (the old vestigial field, now repurposed) via `SetCarryover` — a negative shortfall reduces
   `Allocatable` and must be covered from savings or fresh contributions. Carryover is **no longer a `Contribution`**
   (those forbid negatives): `ContributionsPaidTotal` now excludes the `CarryoverSource` sentinel and `CarryoverTotal =>
   CarriedIn`. `ExpectedClosingBalance` is now `InitialTotal + ContributionsPaidTotal − ExpensesTotal − ExternalOutTotal`
   (carryover already lives in the openings, so no `− CarryoverTotal` term). The serializer folds any legacy
   `CarryoverSource` contribution from older snapshots into `CarriedIn`. **Removed** the vestigial `CarryoverService` +
   `Period.CarryToSavings/CarryToBudget` + `CarryoverTests` (they wrote `CarriedIn` and now conflict). UI: a "From previous
   period" row at the top of the Account-tab Contributions panel shows whenever the leftover is **≠ 0** (negative renders
   as "… shortfall to cover"). **Consequence:** in a clean carry-forward the entered opening ≈ the previous closing, so the
   leftover is ~0 (no row) — it's non-zero only when the real opening differs from the previous expected close.
   **Leftover feeds the contributed pool + cover a shortfall from savings:** the "Contributed" card now shows
   `ContributionsPaidTotal + CarriedIn` (`BudgetingState.TotalContributed`), so a positive leftover is automatically part
   of the spendable pool. A **negative** leftover (shortfall) is covered from the **Savings tab's "Spend savings"** flow:
   the destination `<select>` gains a "From previous period (cover €X)" option when there's a shortfall, dispatched to
   `Period.CoverCarryoverFromSavings(bucket, amount, date)`. That's modelled as a savings movement to the
   `Period.CarryoverSource` pseudo-category (a `-amount` `SavingAllocation` tagged `BudgetCategoryId = CarryoverSource` +
   `CarriedIn += amount`), so it **lists, edits and deletes** like any other spend-savings move (`SavingMovements()` /
   `RemoveSavingMovement` un-covers / `EditSavingMovement` re-covers; `SavingMovementTarget` shows "Bucket → From previous
   period"). The cap is `Period.UnallocatedShortfall = max(0, −Allocatable)` — so **member deposits reduce what needs
   covering automatically** (and editing a cover is capped at the shortfall once that cover is restored). The Account-tab
   "From previous period" row shows the signed leftover and, when `UnallocatedShortfall > 0`, a hint pointing to the
   Savings tab.
6. **Cross-account fund transfer → contribution:** new `Funds/ExternalTransfer` entity + `Period.TransferOut(fundId, amount,
   date, toAccountId, note)` / `RemoveExternalTransfer` / `ExternalOutTotal`. A real outflow: it lowers `FundBalance` and
   `ExpectedClosingBalance` (unlike same-account `FundTransfer`, which is total-preserving). `BudgetingState.TransferToAccount`
   pushes **two snapshots** — this account's outflow, then a `Deposit(currentUser)` into the destination account's current
   period (so it arrives as the signed-in user's contribution). UI: 📤 button in the Funds panel head (shown when the user
   has another same-currency account) → `Modal.TransferOut`; outgoing transfers are listed with a 🗑️ (removing only undoes
   the local outflow, not the deposit already in the other account). Serializer + EF mapping + migration added.
7. **Icon-only buttons + tooltips:** dashboard chrome and inline/form action buttons are now distinct emoji with `title`
   tooltips (➕ add, 👥 invite, 🗑️ delete, ✏️ edit, 📅 dates, ⏭️ next period, 🔁 fund transfer, 📤 send to account, 💰 add
   to savings, ➡️ move savings, etc.). Modal Cancel/Save/Delete buttons keep their **text** labels (clearer in a dialog);
   tab labels stay text too.

## Session 6 — Blazor WASM web host (2026-06-22, roadmap #1 DONE)
Added a second head (`src/FinApp.App.Web`) so the app runs in a browser, reusing **all** UI from `Shared.UI`.
Builds clean; **98 tests still pass** (74 + 5 + 19). Both apps were left running (server :5179, web :5080).
- **New project `src/FinApp.App.Web`** (`Microsoft.NET.Sdk.BlazorWebAssembly`, net9.0) — refs `Shared.UI` + `Contracts`,
  packages `Microsoft.AspNetCore.Components.WebAssembly` (+`.DevServer`) **9.0.6**. No Persistence/SQLite. Added to `FinApp.sln`.
  `Program.cs` registers the same services as MAUI (HttpClient/ClientOptions/FinAppApiClient/AuthState/SyncClient/
  BudgetingState) but **Scoped** and with `WebTokenStore`. `App.razor` (Router → `Shared.UI` assembly + shared `MainLayout`),
  `_Imports.razor`, `wwwroot/{index.html, appsettings.json, css/app.css, css/bootstrap}`.
- **API base URL is now configurable** (no longer MAUI-hardcoded): web reads `wwwroot/appsettings.json` `ApiBaseUrl`
  (falls back to `http://localhost:5179`). `Properties/launchSettings.json` pins the web host to **http://localhost:5080**.
- **`WebTokenStore`** implements `ITokenStore` over browser `localStorage` via `IJSRuntime` — no extra package (the WASM
  counterpart to `MauiTokenStore`/SecureStorage).
- **Refactor to unblock WASM (touches MAUI's shared deps, MAUI still builds):**
  - `AccountSnapshotSerializer` **moved `FinApp.Persistence` → `FinApp.Contracts`** (Contracts now refs Domain). It's pure
    JSON/reflection; this drops the `SQLitePCLRaw.bundle_e_sqlcipher` native dep off the shared-UI/WASM path. `Shared.UI`
    **no longer references `FinApp.Persistence`**. Updated usings in `BudgetingState` + `SnapshotSerializerTests`
    (Persistence.Tests now also refs Contracts); fixed the `<see cref>` in `AccountSnapshotRow`.
  - `MainLayout.razor`(+`.css`) **moved `FinApp.App.Maui/Components/Layout` → `FinApp.Shared.UI/Layout`** so both heads share
    one auth-gated shell. MAUI `Routes.razor` now points at `FinApp.Shared.UI.Layout.MainLayout`.
- **Server CORS** for dev: `Program.cs` adds a `"wasm"` policy (origins from `Cors:AllowedOrigins`, default
  `http://localhost:5080`, `AllowCredentials` for SignalR), `app.UseCors` before auth. One-origin prod hosting stays for #2.
- **Verified end-to-end in a browser:** WASM boots, `WebTokenStore` restored a persisted token from `localStorage`,
  `/me` validated it, and the full Dashboard loaded real account data over CORS (`:5080`→`:5179` preflight returns 204).
- **Run the web app:** `dotnet run --project src\FinApp.App.Web\FinApp.App.Web.csproj` (after the server) → http://localhost:5080.
- **iOS/Android** remain the commented phone TFMs in `FinApp.App.Maui.csproj` — reuse `Shared.UI` as-is when enabling them.

## Session 7 — one-origin deploy + Docker (2026-06-22, roadmap #2 DONE)
Packaged the app to deploy as a **single container** that serves the API + SignalR hub + WASM UI on one origin
(no CORS in prod). **98 tests still pass.** Docker isn't installed on this machine, so the image build itself is
unverified locally — but one-origin hosting was verified by running the server in Development.
- **Server hosts the WASM (`FinApp.Server`):** added `ProjectReference` to `FinApp.App.Web` +
  `Microsoft.AspNetCore.Components.WebAssembly.Server` 9.0.6. `Program.cs` now does `UseBlazorFrameworkFiles()` +
  `UseStaticFiles()` (before auth) and `MapFallbackToFile("index.html")` (after the hub) for SPA routing. **CORS is now
  Development-only** (`if (app.Environment.IsDevelopment()) app.UseCors(...)`). Publishing the server bundles the WASM
  client's `wwwroot`/`_framework` automatically via the project ref.
- **Client same-origin by default:** `FinApp.App.Web/Program.cs` uses `ApiBaseUrl` when set, else
  `builder.HostEnvironment.BaseAddress`. `wwwroot/appsettings.json` → `ApiBaseUrl: ""` (prod one-origin);
  `wwwroot/appsettings.Development.json` → `http://localhost:5179` (local cross-origin two-terminal dev).
- **Server config split:** dev-only `Urls` (`:5179`) + `Cors:AllowedOrigins` (`:5080`) moved to a new server
  `appsettings.Development.json`; prod `appsettings.json` is clean (binds via `ASPNETCORE_URLS`, default `http://+:8080`
  in the image). **JWT guard:** the server **refuses to start outside Development** if `Jwt:Key` is empty/placeholder/<32
  chars — set `Jwt__Key` at runtime.
- **Container:** multi-stage [`Dockerfile`](Dockerfile) (SDK stage installs `wasm-tools`, publishes the server) + `.dockerignore`.
  SQLite at `/data/finapp-server.db` on a **mounted volume** (`ConnectionStrings__FinApp` env, default points there);
  EF migrations apply on startup. Full deploy guide + per-platform notes (Fly.io/Render/Azure/VPS) in [`DEPLOY.md`](DEPLOY.md).
- **Verified (Development run of the server):** `GET /` → 200 WASM shell; `/_framework/blazor.webassembly.js` → 200;
  client `appsettings.json` served; `GET /accounts` → 401 (API routing + auth intact); `GET /some/client/route` → 200 shell
  (SPA fallback). **Not verified locally:** `docker build` (no Docker here) and a real cloud deploy (needs your host creds).
- **Run one container locally (on a machine with Docker):**
  `docker build -t finapp . && docker run -p 8080:8080 -e Jwt__Key="$(openssl rand -base64 48)" -v finapp-data:/data finapp`
- **Platform deploy kits added:** `fly.toml` (Fly.io, scale-to-zero), `deploy/oracle/` (Oracle Cloud Always Free —
  Docker Compose + Caddy auto-HTTPS), and `deploy/cloudrun/` (the chosen path — see below). `.gitattributes` forces LF on
  `.sh`/Dockerfile/Compose/Caddyfile; `.env` is gitignored. CI builds + pushes `ghcr.io/shonzi91/finapp` on push to main
  (`.github/workflows/docker-publish.yml`) for the VM/registry paths.
- **CI image-build gotchas (fixed):** Blazor WASM publish in `dotnet/sdk:9.0` needs `python` for the Emscripten relink
  (install `python3 python-is-python3`), and the relink itself is slow → set `<WasmBuildNative>false</WasmBuildNative>`
  in `FinApp.App.Web.csproj` to skip it. Also dropped the `type=gha` build cache (caused `DeadlineExceeded`).
- **Oracle free VM was abandoned:** the Always-Free shape only gave ~500 MB RAM → OOM-killed `dnf`/builds and wedged SSH
  repeatedly. Root lesson: SQLite needs a persistent disk + always-on process, which forces a fragile tiny free VM.
- **DB is now provider-switchable (Session 7b):** `FinApp.Server` supports **SQLite** (default; dev/tests/MAUI) and
  **Postgres** via `Database__Provider=Postgres` + `ConnectionStrings__FinApp=<Npgsql>` (added `Npgsql.EntityFrameworkCore
  .PostgreSQL` 9.0.4). Postgres uses `Database.EnsureCreated()` (the EF migrations are SQLite-specific; cloud DB is fresh);
  SQLite still uses `Migrate()`. Model was already provider-agnostic (Money→text, all `DateTimeOffset` are UtcNow). 98 tests
  still green. **Chosen deploy: Google Cloud Run + free Neon Postgres** (`deploy/cloudrun/README.md`) — managed, auto-HTTPS,
  scale-to-zero, `gcloud run deploy --source .` builds via Cloud Build (no local Docker). Must run `--max-instances 1`
  (SignalR has no backplane). **DEPLOYED & LIVE (2026-06-22):** https://finapp-85638328674.europe-west1.run.app
  (GCP project `finapp-1111`, region europe-west1, Neon Postgres eu-central-1). Verified: `/`→200 WASM shell, `/accounts`→401,
  startup `EnsureCreated()` succeeded against Neon (proves DB connectivity).
  - **Gotcha fixed during deploy:** Neon hands out a `postgres://` URI, but Npgsql only parses key-value strings → startup
    crash. `Program.cs` now normalizes a `postgres://`/`postgresql://` URI to `NpgsqlConnectionStringBuilder` form.
  - **SECURITY TODO:** the Neon DB password was surfaced in a Cloud Run log read during debugging (so it's in that session
    transcript). Rotate it in the Neon dashboard and redeploy with the new `ConnectionStrings__FinApp`.
  - Redeploy/update: `gcloud run deploy finapp --source . --region europe-west1` (reuses env vars). Secrets currently passed
    as env vars; move to Secret Manager for hardening.
- **UX polish (2026-06-22, live as revision finapp-00003):** `Dashboard.razor` `Run()` helper now guards against
  re-entrant clicks (no double-submits), shows a floating "Saving…" pill + dims/locks the dash during the server
  round-trip (`StateHasChanged()` + `await Task.Yield()` to paint first), and maps common failures (409 conflict / 401
  expired / network `HttpRequestException`) to human messages via `Describe(ex)` instead of raw `ex.Message`. Dismiss (×)
  on error banners (`.alert-x`). New scoped CSS in `Dashboard.razor.css` (`.saving-pill`, `.dash.is-busy`, `.alert-x`).

## Next sessions roadmap (planned 2026-06-19) — confirm scope/order with the user before starting

These are the agreed next big pieces, roughly in dependency order. Each is a multi-step feature; pick one, plan it, then build.

### 1. Web version of the UI (Blazor WASM), structured so iOS/Android follow — ✅ DONE (Session 6, 2026-06-22)
- **Most UI is already shareable.** All pages/components/state live in `src/FinApp.Shared.UI` (`Dashboard.razor`, the
  components, `BudgetingState`, `FinAppApiClient`, `AuthState`, `SyncClient`). The MAUI app is just a *host*. The web app is
  a second host; iOS/Android are MAUI phone TFMs (one commented line in `FinApp.App.Maui.csproj`) — so keep **all UI in
  Shared.UI** and every head reuses it. Don't fork UI per platform.
- **New project `src/FinApp.App.Web`** (Blazor WASM). It references `FinApp.Shared.UI` + `FinApp.Contracts` and registers
  the same services. The client is **fully server-backed** (REST + SignalR) — it does **not** use `FinApp.Persistence` /
  SQLite / SQLCipher, so WASM needs no native SQLite. Keep it thin.
- **Platform service shims to provide for WASM:** `ITokenStore` (today MAUI `MauiTokenStore`/SecureStorage → browser
  `localStorage` via JS interop or `Blazored.LocalStorage`); the API/SignalR **base URL** (today hardcoded to
  `http://localhost:5179` in `MauiProgram.cs` → make it configurable, e.g. from `appsettings`/build config). Verify SignalR
  works under WASM (it does, via WebSockets; check the `?access_token=` query-string auth path still applies).
- **Server CORS**: if web is served from a different origin than the API, add a CORS policy in `FinApp.Server` for that
  origin (preflight + SignalR). Simplest is to avoid CORS entirely by having `FinApp.Server` host the WASM static files
  (see deploy item).
- iOS/Android later = uncomment the phone TFMs, provision the SDKs + signing, and reuse Shared.UI as-is.

### 2. Deploy the web app together with the database — ✅ DONE (Session 7, 2026-06-22) — see DEPLOY.md
- **One-origin deploy (recommended):** have `FinApp.Server` serve the Blazor WASM build from `wwwroot`
  (`UseBlazorFrameworkFiles()` + `MapFallbackToFile("index.html")`) so a single deployment serves the API + SignalR hub +
  web UI on one origin (no CORS). Alternatively host WASM on static/CDN and keep the API separate (then needs CORS).
- **Database:** server uses **file-based SQLite** (`finapp-server.db`) — fine for a single instance but needs a
  **persistent volume** and backups in prod. For multi-instance/scale, plan a move to **Postgres/SQL Server** (the EF model
  is mostly portable; `MoneyConverter` stores text; watch SQLite-specific migration SQL). The account body is stored as an
  **opaque snapshot blob**, currently **plaintext** — E2E encryption is still a pending hardening item.
- **Prod checklist:** replace the dev `Jwt:Key` placeholder in `appsettings.json`; enable HTTPS/TLS; set the client's API
  base URL to the deployed origin; container (Dockerfile) or PaaS (Azure App Service/Container Apps, Fly.io, Render, or a
  VPS with a persistent disk). `AccountStore.Migrate()` already applies EF migrations on startup.

### (NEW) Account reports / health / insights tab
A dedicated tab for **reports, financial health, analysis and insights** per account: e.g. spend-by-category
trends across periods, budget-adherence/overspend history, savings-rate trajectory, fund-balance over time,
income-vs-expense, top categories, month-over-month deltas, and simple insights/alerts. Reads from the existing
period aggregate (budgets/expenses/savings/contributions already there) — mostly a new read-only tab + a few
derived metrics + charts. (Added 2026-06-25 at the user's request.)

### (NEW) Savings configuration per account — enable savings at all, + involvement mode
Added 2026-06-26 at the user's request. **The user's vision (two account-level settings, set at account creation and
editable later in account editing):**
1. **Savings on/off for the account.** On account creation the user chooses whether this account has a **Savings tab at
   all**. Some accounts are pure **budget/expense flow** — a user who doesn't want to deal with savings shouldn't see it.
   New flag `Account.SavingsEnabled` (bool, default true). When false: hide the Savings tab and all savings UI/actions;
   `Free = Current` (no savings term); the account is just funds + budgets + expenses + contributions + transfers.
2. **Savings involvement mode** (only when savings is enabled) — a toggle between the two models below.
Both flags are picked in the **create-account** flow and changed in the **edit-account** modal (`Account.Rename`/edit
path). Switching modes on an account with existing savings needs a migration of its data (earmark↔fund-attributed) —
think through that conversion (e.g. on enabling discipline, pull each bucket's balance out of a chosen/default fund).

**The involvement-mode toggle (the harder half):** switch savings from the current **earmark** model (model A: a bucket
is a label over cash that stays in the funds; `Free = Current − savings`) to a **fund-attributed** model (model B: saving
physically moves money **out of a fund into the bucket**, so the bucket is a real second container — essentially a fund
that can't go below 0). The point of B: enforce discipline — saved money leaves the spendable pool, so the user is forced
to keep spending within what remains.
- **New account flags** `Account.SavingsEnabled` (default true) + `Account.DisciplinedSavings` (default false) — both
  serializer + EF column + migration. Default accounts keep today's behavior untouched — this is purely additive.
- **In-app clarity (important — the whole model has confused even the dev):**
  - **Savings tab: show a plain-language banner stating which mode the account is in and what it means.** Earmark mode:
    "Savings here is a label on money that stays in your funds — saving sets it aside on paper but the cash is still in
    your accounts." Disciplined mode: "Saving moves real money out of your funds into this bucket, so it leaves your
    spendable balance." Keep it one or two sentences, always visible at the top of the Savings tab.
  - **Budgets: make clear budgets are never real money / never touch funds.** Add a short note on the Budgets tab (and/or
    the budget modals) like "Budgets are a spending plan only — they don't move or hold money; your funds and balances are
    unaffected by what you budget." This kills the recurring confusion that budgeting changes your cash.
- **When on**, saving/releasing becomes a transfer between a fund and the bucket: it lowers/raises that **fund's balance**
  (and so `ExpectedClosingBalance`/`Current`). The "Transfer bucket" dropdown's **Funds** section (the UI we discussed)
  is how you move value fund↔bucket, clamped so a bucket can't go negative. "Add to savings" picks a source fund.
- **Ripple to re-derive for mode B (the reason it's a real feature, not a tweak):** `ExpectedClosingBalance` subtracts
  saved money (it left the funds); `FundBalance` drops on save; **`Free = Current`** (drop the `− savings` term — savings
  is no longer inside Current); `Deficit`/"not backed by cash" largely disappears (you can't save cash you don't have);
  the savings rate keys off transfers-in; period-start carryover tracks **two** kinds of container (funds + buckets).
  Buckets need their own carried balance across periods. The reports/insights and the budget caps that compare to closing
  all read the new closing. Worked example: Bank 1000, save 300 → A: Bank 1000, Current 1000, free 700; B: Bank 700,
  Vacation 300, Current 700, free 700 (no `− savings`).
- **Build approach:** branch the money-model reads on the flag (a small strategy seam in `Period`/`BudgetingState`),
  keep all model-A tests green, add a parallel model-B test suite. The UI shows buckets as a separate "saved" pot and
  relabels "Current" as spendable when the flag is on. **Confirm scope before starting — it's a model-level change.**

### (NEW) Excel import/export per account — one sheet per period
**Export ✅ DONE (Session 11k, server-side ClosedXML).** Import still TODO. Export an account to an `.xlsx` (a sheet per
period: opening balances, contributions, budgets, expenses, savings, transfers) and re-import it. **Decision needed for import: where to compute.**

**Export (done):** `GET /accounts/{id}/export` → `AccountExportService` (server) deserializes the snapshot via
`AccountSnapshotSerializer` and builds the workbook with **ClosedXML** (added to `FinApp.Server`; v0.105). One "Account"
overview sheet + a sheet per period (named `NN yyyy-MM`). Client: `FinAppApiClient.ExportAccountAsync` downloads the
authorized bytes; `Dashboard.ExportAccount` → JS `finappDownloadFile` (base64→Blob→anchor) saves the file. UI: 📊 button
in the account-ops bar. Tests: `ExportApiTests` (real xlsx via `PK` header; empty account → 404). 21 server tests.
**Import (TODO):**
- **Option A — server-side (recommended, simplest):** add `GET /accounts/{id}/export` (build workbook with **ClosedXML**;
  server can deserialize the snapshot via `AccountSnapshotSerializer` in Contracts) and `POST /accounts/{id}/import`
  (parse → rebuild account → save snapshot). Download via a normal link; upload via a file input. **Tension:** the server
  currently treats the snapshot as an opaque blob (future E2E-encryption goal) — doing xlsx server-side reads the data in
  clear, so if E2E lands this must move client-side.
- **Option B — client-side (WASM):** generate/parse in the browser via a JS lib (SheetJS) over JS interop, or
  `DocumentFormat.OpenXml` in .NET (works in WASM but verbose). Keeps data on the client; heavier bundle/interop.
- **Schema round-trip is the hard part:** ids must survive (or be regenerated consistently) so categories/funds/members
  line up on import; decide whether import **replaces** the account or **merges**. Start with **export** (read-only, safe),
  then import. Confirm A vs B before building.

### 3. Customizable notifications, per account, per user
- **Domain hooks already exist** to drive triggers: budget `AlertThreshold` + `NotifyOnEveryExpense`, saving
  `AlertThreshold` + `NotifyOnMilestone`, plus `Period.Deficit` (overspend) and savings-goal progress.
- **Preferences are per-(user, account)** so they must live **server-side**, NOT in the shared account snapshot (the
  snapshot is common to all contributors). Add a server table keyed by `(UserId, AccountId)` holding which events the user
  wants: budget-threshold reached, overspend/`Deficit`, savings-goal milestone, deposit by another member, period-end
  reminder, invitation received — plus channel/cadence.
- **Trigger evaluation** on `PUT /accounts/{id}/snapshot`: diff the new snapshot vs the prior one server-side, compute which
  thresholds were crossed, and emit notifications to the affected users' preferences.
- **Delivery channels:** in-app (a notifications panel + live `SignalR` push — infra already there), **Web Push** for the
  WASM app, and/or email (queue). Start with in-app + SignalR, then add Web Push/email.

### 4. Adjust UI + fix bugs (ongoing)
- **Responsive/mobile:** the icon toolbar and the 4-card grid need a pass for phone/web narrow widths (web + future
  iOS/Android form factors).
- **Carry-over math follow-ups from Session 5 (verify in the live app):**
  - Savings-rate denominator now = member deposits only (carryover excluded, since it's in `CarriedIn` not
    `ContributionsPaidTotal`) — confirm that's the desired "income-only" behaviour.
  - `MaxAdditionalSavings` can overstate headroom by ~2× when savings is drawn negative (cover-from-savings /
    `ConvertSavingToBudget` drawdowns push `SavingsNetTotal` below 0) — audit the envelope math.
  - `HasUnallocatedFunds` deposit-block may be too eager now that opening money/carryover counts.
  - **Backfill** existing periods' carryover to the current `opening(n) − closing(n−1)` rule (offered to the user, not yet
    run) — a one-time recompute over stored snapshots.
- ~~`git init` the repo~~ ✅ done — repo is on GitHub `shonzi91/FinApp` (rename to Budgiely still pending). A regression
  test sweep is still worthwhile.

## Still open (smaller items)
- The vestigial `Period.CarriedIn` column is now **live** (repurposed as the signed carryover) — no longer cleanup; the old
  `CarryoverService`/`CarryToBudget`/`CarryToSavings`/`CarryoverTests` were deleted this session. `State.BudgetedCategories`
  is still unused and can be removed.

## UI layout (Dashboard.razor)
Header = account switcher (✏️ rename / + add / 🗑️ delete) + period nav (◀ ▶) + inline closing-balance & "Start next month →". Below it, **4 tabs**:
1. **Account** — totals cards (Contributed / Spent / Budgeted / **Saved this period + %**) + overspend banner, **Funds panel** (tree with sub-funds + informative values), **contributions** (a "From previous period" carryover row + per-member amount/Deposit, each deposit ✏️/🗑️-editable), recent expenses. (No carryover-allocation row or reconciliation alert — superseded by the period-start fund sync.)
2. **Budgets** — category tree; inline ✏️/➕/🗑️ + **＋ expense**; expenses listed beneath each category. (No dates/reschedule control.)
3. **Expenses** — add-expense form + all expenses newest-first (inline ✏️/🗑️).
4. **Savings** — buckets with goal progress bars + ✏️/🗑️ + "+ bucket" (a starting balance can be set during setup); period & all-time savings %; "Add to savings"; "Spend savings" = one grouped destination dropdown (budgets + other buckets) + a single **Move** button.

## Implemented features
- Accounts: multiple, with header switcher; **add / rename / delete** (delete cascades all periods/data). First-run "create your first account" screen (no demo seed). Currency is fixed once created.
- Periods: navigation (◀ ▶), reschedule dates (cascades to later periods keeping lengths), start-next-period via **confirmation modal** with copy-budgets checkbox (carries closing balance into the default fund), reconciliation gate (blocks contributions until prior period reconciles).
- Budgets: **category tree** with inline ✏️ edit / ➕ add-sub / 🗑️ delete + ＋ expense; coverage % bars + threshold/overspend colors. Expenses listed under each category.
- Categories: add (with optional budget), rename, remove (blocked if a budget/expense/child references it).
- Expenses: add anywhere; **edit & remove inline** in all three places (account/budgets/expenses tabs) via modals; only on an open period. Editing a savings-funded expense keeps its savings link; removing it restores the drawdown (linked by `SavingAllocation.SourceExpenseId`).
- Contributions: **direct deposits only** (no pledges/due-dates/pending reminders as of Session 3) — per-member amount + Deposit, each deposit ✏️/🗑️-editable; **deposit blocked while unallocated funds exist** (`State.HasUnallocatedFunds`).
- Savings: buckets **add/edit/delete** (remove blocked if it has activity/sub-buckets); optional **goal** (target + alert % + notify) with progress bars; **period & all-time savings rate** (excludes a bucket's setup-time `InitialAmount`). A bucket can carry a pre-app **starting balance** (setup only). **Add to savings** deposits are ✏️/🗑️-editable (Session 3). **Spend savings** = one grouped destination (a budget via `ConvertSavingToBudget`, or another bucket via `TransferSavings`) + a single Move button. (`ConvertSavingToExpense`/`BudgetingState.SpendFromSavings` still exist but the "Spend now" UI was dropped in Session 2.)
- **Funds** (replaces old `FundType` enum): account-level entities, **add/rename/delete**; **informational sub-funds** (one level, `ParentId`, no balance — money/calc stays on the parent). Removal is blocked by expenses/transfers/sub-funds or being the only fund; an **opening balance is moved to another fund** on removal (Session 3) rather than blocking. Per-period **transfers** between funds (`Period.TransferFunds`) — dated ledger, total-preserving, never affects closing balance/reconciliation. Per-fund position = opening + transfers-in − transfers-out − spending. Opening balance editable per fund per period.
- **Carryover** (redesigned through Session 5): the "From previous period" leftover = `thisOpening − previousClosing`,
  stored **signed/unclamped** in `Period.CarriedIn`, set at "Start next month". It feeds `Allocatable`/the Contributed pool;
  a positive leftover is auto-spendable, a negative leftover (shortfall) reduces what's allocatable and is **covered from a
  savings bucket** via the Savings tab's "Spend savings → From previous period" movement (capped at `UnallocatedShortfall`,
  which member deposits reduce automatically). Excluded from `ExpectedClosingBalance` (already sits in the openings).

## Build / run / test
```powershell
cd C:\Projects\FinApp
dotnet test tests\FinApp.Domain.Tests\FinApp.Domain.Tests.csproj
dotnet test tests\FinApp.Persistence.Tests\FinApp.Persistence.Tests.csproj
dotnet test tests\FinApp.Server.Tests\FinApp.Server.Tests.csproj
dotnet run --project src\FinApp.Server\FinApp.Server.csproj      # the sync server/API + SignalR (:5179)
dotnet run --project src\FinApp.App.Web\FinApp.App.Web.csproj    # Blazor WASM web head (:5080) — run the server first
dotnet build src\FinApp.App.Maui\FinApp.App.Maui.csproj -f net9.0-windows10.0.19041.0
.\src\FinApp.App.Maui\bin\Debug\net9.0-windows10.0.19041.0\win10-x64\FinApp.App.Maui.exe
```
All 98 tests currently pass (74 domain + 5 persistence + 19 server).
EF migrations: `dotnet ef migrations add <Name> --project src\FinApp.Persistence` (tool installed; `AccountStore.Migrate()` applies).

## Gotchas (important)
- **Corporate NuGet feed** (`proget-dev.btigroup.io`) times out. A solution-local `NuGet.config` pins restore to nuget.org — keep it.
- **EF Core version:** pinned to **9.0.6** (latest 10.x is net10-only). MAUI workload is installed.
- **MAUI target trimmed to Windows** (`net9.0-windows10.0.19041.0`) so it builds without Android/iOS SDKs. Phone targets are one commented line in the csproj.
- **`GetLatestMSVCVersion` build failure** (seen esp. from Visual Studio deploy): unpackaged Windows MAUI apps default to **Windows App SDK self-contained**, which bundles the VC++ runtime and needs the MSVC C++ toolset ("Desktop development with C++"), absent on this machine. Fix in `FinApp.App.Maui.csproj`: `WindowsAppSDKSelfContained=false` + `SelfContained=false` for the Windows TFM (framework-dependent — relies on the WinAppSDK runtime being installed, which it is here; app builds + runs). For a standalone/distributable build instead, install the C++ workload and remove those two lines.
- **EF Core migrations are now in use** (landed 2026-06-17): `AccountStore.Migrate()` (was `EnsureCreated()`/`PatchSchema()`, both removed). Design-time factory `FinAppDbContextFactory` builds schema-only options (no SQLCipher key). Add a migration for any schema change; it applies on next app/server start. The client DB still lives at `…\com.companyname.finapp.app.maui\Data\finapp.db`; to start clean, move it aside (`finapp.db.premigrate-<stamp>` was the M-migrations cutover backup).
- DB is **encrypted** (client) / plain SQLite (server). Can't read the encrypted file without the key. The wrong-key test proves encryption.
- **`FundType` enum was removed.** Funds are now `Guid`-referenced entities (non-FK scalar on Expense/InitialBalance/FundTransfer, same pattern as `CategoryId`). Tests pass throwaway `Guid.NewGuid()` where the specific fund is irrelevant.

## Current state
- **EF migrations landed**: Initial, AddUsersAndSharing, AddAccountSnapshots, AddSavingInitialAmountAndSubFunds,
  DropContributionPledge, AddInformativeInitialBalance, **AddExternalTransfersAndSavingMovementLinks** (latest, Session 5).
  Applied on app/server start via `AccountStore.Migrate()`. Client DB backup at the migrations cutover:
  `finapp.db.premigrate-20260617-104237`. Don't re-seed; user sets up their own account.
- **Multi-user feature complete (M0–M3) and verified.** Five rounds of budgeting changes have since landed
  (Post-M3 / Session 2 / 3 / 4 / 5 + the carryover redesign). **98 tests pass** (74 domain + 5 persistence + 19 server).
  Plan file: `C:\Users\stoyan.s\.claude\plans\glistening-hopping-lamport.md`.
- Server runs on `http://localhost:5179`; server + MAUI app were left running at the end of this session.
- **Next:** see "Next sessions roadmap" above — web (Blazor WASM) UI, deploy server+DB, per-account/user notifications, UI/bug pass.
- Working branch: none. Standalone folder (not the Global.Data.Api repo). **Not yet `git init`'d** — no version history, so this HANDOFF + the dated session sections are the change log.

## Next steps / open items
1. **Multi-user feature is complete (M0–M3).** Possible polish if revisiting: snapshot save on 409 conflict currently surfaces an error (the live `AccountChanged` handler re-pulls) — consider a smarter merge/retry; per-mutation full-snapshot PUT is simple but chatty (fine at this scale); server JWT key is a dev placeholder.
2. **Future sharing hardening:** Facebook/Google OAuth login; email invitations; **E2E-encrypted snapshots** (swap `AccountSnapshot.Payload` for ciphertext — contract already opaque); offline replica + conflict merge.
3. **Optional fund refinement:** contributions/deposits aren't fund-attributed, so a fund's shown position is its *spending* position, not a share of the closing balance. Attribute deposits to a target fund to make per-fund balances sum to the period total. Only if desired.
4. **Notifications** (local reminders for reconciliation; budget & savings-goal threshold alerts) — domain hooks exist (budget `AlertThreshold`/`NotifyOnEveryExpense`, saving `AlertThreshold`/`NotifyOnMilestone`). Pledges/due-dates were removed in Session 3, so deposit-deadline reminders no longer apply.
5. Blazor WASM client; then phone targets.

## Interpretations made (confirm if revisiting)
- Rescheduling a period shifts **itself + later** periods (keeps their lengths); earlier periods untouched.
- Savings cap = contributed + carried-in − budgeted.
- Carryover pool = previous period's leftover; allocations land in the **current** period.
- **Spend savings**: convert-to-budget releases the earmark at conversion; under-spending a budget flows the remainder into next period's carryover. (The one-off "Spend now" path was dropped from the UI in Session 2.)
- **Fund transfers** are total-preserving and modelled as a ledger; they never appear in `ExpectedClosingBalance`.
