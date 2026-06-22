# FinApp

A privacy-first budgeting & expense tracker. Money is organised **period by period**
inside first-level accounts (Personal, Shared, Family), with budgets, an expense ledger,
and accumulating savings buckets. Account data is **stored locally** on each device; an
optional server only relays **end-to-end-encrypted** change events so multiple people can
share an account and stay in sync.

## Tech decisions

| Concern | Decision | Why |
|---|---|---|
| Language/stack | **.NET 9 / C#** | Matches the team's expertise. |
| UI | **Blazor**, shared across **MAUI Blazor Hybrid** (mobile/desktop) and **Blazor WebAssembly** (web) | One UI codebase, two hosts. Native first (strongest local-privacy story). |
| Local storage | **SQLite + EF Core**; SQLCipher-encrypted on native, OPFS-persisted on web | Same data layer everywhere; real at-rest encryption for financial data. |
| Multi-user sync | **ASP.NET Core + SignalR** relay of **E2E-encrypted** event blobs | Native .NET, real-time, server never sees plaintext. (MQTT/RabbitMQ only if scale later demands a true broker.) |
| Conflict handling | Append-only **ledger** for expenses/contributions; last-writer-wins for settings | Merges cleanly across devices; makes period reconciliation auditable. |
| Notifications | On-device local reminders + push (FCM/APNs) for cross-user events | Reminders work offline; live events arrive when the app is closed. |

> **Honest caveat:** a browser web app can't match native for guaranteed local privacy
> (storage can be evicted, weaker sandbox). The native app is the privacy-strong client;
> the web/PWA is a convenience client.

## Solution layout

```
FinApp.sln
src/
  FinApp.Domain/            ← pure C# domain model + business rules (no UI, no storage)
  FinApp.Persistence/       ← EF Core + SQLite (SQLCipher-encrypted), maps the domain aggregate
  FinApp.Shared.UI/         ← shared Blazor components + app state (reused by every client)
  FinApp.App.Maui/          ← MAUI Blazor Hybrid host (Windows target enabled today)
tests/
  FinApp.Domain.Tests/      ← xUnit tests for the rules
  FinApp.Persistence.Tests/ ← encrypted save/reload round-trip tests
```

Planned next layers: `FinApp.Sync.Server` (SignalR), `FinApp.Web` (Blazor WebAssembly).

## Domain model (implemented)

- **Account** — first-level account; owns members, the category & savings-category trees, and periods.
- **Period** — `from→to`; owns opening balances (per fund: Bank/Cash/Wallet), member contributions
  (pledged vs paid), budgets, the expense ledger, and savings movements.
- **Category / SavingCategory** — trees (sub-categories roll up to parents).
- **Budget** — per-category allocation with alert threshold and notify-on-every-expense flag.
- **Expense** — immutable ledger entry; may be a "saving → expense" conversion.
- **Money** — value object, 2-dp banker's rounding, currency-safe arithmetic.

### Business rules covered by tests
- **Reconciliation (feature 4):** new period's opening balance must equal previous period's
  `opening + paid contributions − expenses`; contributions are blocked until discrepancies clear.
- **Budget coverage (feature 6):** sub-category expenses roll up to the parent budget; %, remaining,
  over-budget and threshold-reached flags for charts/alerts.
- **Savings (feature 8):** buckets accumulate across periods; savings rate = net saved ÷ paid
  contributions; converting a saving to an expense draws down the bucket and records a real expense.
- **Copy budgets forward (feature 5):** `StartPeriod(copyBudgetsFromPrevious: true)`.

## Build & test

```bash
dotnet build
dotnet test
```

> `NuGet.config` pins restore to nuget.org (this repo does not use the corporate Proget feed).

## Persistence

`FinApp.Persistence` maps the rich domain aggregate directly with EF Core:
- `Money` is value-converted to a single text column (keeps every entity constructor-bindable).
- Collections map through their private backing fields; computed properties are `Ignore`d.
- The SQLite file is **SQLCipher-encrypted**; the key lives in the OS keystore (MAUI `SecureStorage`,
  DPAPI-backed on Windows). A round-trip test asserts a wrong key cannot open the database.

On the MAUI host the DB lives in the app-data directory and is created/seeded on first launch;
edits persist immediately and survive restarts.

## Roadmap
1. ✅ Domain model + rules + tests
2. ✅ EF Core + SQLite persistence (SQLCipher-encrypted) + round-trip tests
3. ✅ Shared Blazor UI + MAUI host:
   - Multiple accounts with a switcher; each account has its own periods.
   - Period navigation (◀ ▶) + rescheduling (cascades to later periods, keeping their lengths).
   - Budgets shown as a category **tree** with inline edit/add-sub/delete buttons; add/edit/delete in modals.
   - Budget CRUD; category add/rename/remove (removal blocked while a budget/expense/child references it).
   - Contributions with pledge **due dates**; savings allocate/spend, capped at contributed − budgeted.
   - **Carryover**: the previous period's total leftover (Σ budgeted − actual) can be allocated
     (add or take back) into this period's savings buckets or budgets.
4. SignalR sync server + E2E encryption + offline catch-up
5. Notifications (local reminders + push)
6. Blazor WebAssembly / PWA client
7. Phone targets (Android/iOS) on the MAUI host
