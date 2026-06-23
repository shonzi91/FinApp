# FinApp — session handoff

Last updated: 2026-06-19. Read this + [README.md](README.md) + recent `git log` to catch up, then confirm the next step with the user before coding.

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
- **`git init`** the repo for real version history (currently none — this HANDOFF is the only change log). Add a regression
  test sweep.

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
