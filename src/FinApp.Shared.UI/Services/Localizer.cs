using Microsoft.JSInterop;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Minimal UI localization. English text is the lookup key, so untranslated strings fall back to
/// English automatically and there are no separate key names to maintain — only one Bulgarian map.
/// The chosen language is persisted in the browser's localStorage. Components inject this, render
/// <c>Loc.T("English")</c>, and re-render on <see cref="Changed"/>.
/// </summary>
public sealed class Localizer(IJSRuntime js)
{
    private const string StorageKey = "finapp-lang";

    public string Culture { get; private set; } = "en";
    public event Action? Changed;

    /// <summary>Load the saved language once at startup (call from the layout's OnInitializedAsync).</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var saved = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (saved is "en" or "bg" && saved != Culture)
            {
                Culture = saved;
                Changed?.Invoke();
            }
        }
        catch { /* storage unavailable — stay on English */ }
    }

    public async Task SetCultureAsync(string culture)
    {
        if (culture is not ("en" or "bg") || culture == Culture) return;
        Culture = culture;
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, culture); }
        catch { /* ignore */ }
        Changed?.Invoke();
    }

    /// <summary>Translate <paramref name="en"/> to the current culture, falling back to the English text.</summary>
    public string T(string en) => Culture == "bg" && Bg.TryGetValue(en, out var v) ? v : en;

    public string this[string en] => T(en);

    // English -> Bulgarian. Keys are the exact English strings rendered in the UI.
    private static readonly Dictionary<string, string> Bg = new(StringComparer.Ordinal)
    {
        // App chrome
        ["Hello,"] = "Здравей,",
        ["Sign out"] = "Изход",
        ["Loading…"] = "Зареждане…",
        ["Saving…"] = "Запазване…",
        ["Dismiss"] = "Затвори",
        ["Couldn’t do that."] = "Неуспешно действие.",

        // Auth
        ["Private, shared budgeting. Sign in or create an account to begin."] =
            "Личен, споделен бюджет. Влез или създай профил, за да започнеш.",
        ["Sign in"] = "Вход",
        ["Create account"] = "Създай профил",
        ["Username or email"] = "Потребител или имейл",
        ["Password"] = "Парола",
        ["Username"] = "Потребител",
        ["Email"] = "Имейл",
        ["you@example.com or username"] = "you@example.com или потребител",
        ["Your password"] = "Твоята парола",
        ["Pick a username"] = "Избери потребителско име",
        ["At least 8 characters"] = "Поне 8 символа",
        ["Signing in…"] = "Влизане…",
        ["Creating…"] = "Създаване…",
        ["Password must be at least 8 characters."] = "Паролата трябва да е поне 8 символа.",
        ["Couldn’t reach the server. Check your connection and try again."] =
            "Сървърът е недостъпен. Провери връзката и опитай отново.",

        // First run
        ["Welcome to FinApp"] = "Добре дошъл във FinApp",
        ["Create your first account to get started (e.g. Personal, Shared, Family)."] =
            "Създай първия си профил, за да започнеш (напр. Личен, Споделен, Семеен).",
        ["Account name"] = "Име на профил",
        ["Currency"] = "Валута",
        ["It starts with a few starter categories and the current month’s period — you can change everything."] =
            "Започва с няколко начални категории и периода за текущия месец — всичко може да се променя.",

        // Tabs
        ["Account"] = "Профил",
        ["Budgets"] = "Бюджети",
        ["Expenses"] = "Разходи",
        ["Savings"] = "Спестявания",

        // Account-tab cards + balances
        ["Current"] = "Текущо",
        ["Closed on"] = "Затворено с",
        ["Spent"] = "Похарчено",
        ["Budgeted"] = "Бюджетирано",
        ["Saved this period"] = "Спестено този период",
        ["of contributions"] = "от вноските",
        ["Opening"] = "Начално",
        ["Active"] = "Активен",
        ["Closed"] = "Затворен",
        ["shared"] = "споделен",

        // Panels / headings
        ["Funds"] = "Сметки",
        ["Transfer money"] = "Прехвърли пари",
        ["Other accounts"] = "Други профили",
        ["Move money between your funds — the total is unchanged, only where it sits."] =
            "Премести пари между сметките си — общата сума не се променя, само къде стои.",
        ["Sending to another account leaves this one as an outflow."] =
            "Изпращането към друг профил напуска този като изходящо.",
        ["Available to send:"] = "Налично за изпращане:",
        ["cash not earmarked for savings"] = "пари, незаделени за спестявания",
        ["not backed by cash"] = "непокрити с налични пари",
        ["Category & fund"] = "Категория и сметка",
        ["New fund"] = "Нова сметка",
        ["Transfer from this fund"] = "Прехвърли от тази сметка",
        ["Deposit to this fund"] = "Внеси в тази сметка",
        ["Transfer from"] = "Прехвърли от",
        ["To"] = "Към",
        ["Available in this fund:"] = "Налично в тази сметка:",
        ["what this fund holds, not earmarked for savings"] = "каквото е в сметката, незаделено за спестявания",
        ["Opening balance this period"] = "Начален баланс този период",
        ["Opening balance this period (optional)"] = "Начален баланс този период (по избор)",
        ["Manage categories"] = "Управление на категории",
        ["Add a deposit"] = "Добави вноска",
        ["Contribution categories"] = "Категории вноски",
        ["Edit"] = "Редактирай",
        ["Destination"] = "Получател",
        ["Move"] = "Премести",
        ["Spend"] = "Похарчи",
        ["Savings activity"] = "Спестовна дейност",
        ["Available to save:"] = "Налично за спестяване:",
        ["the money in the account, minus what's budgeted and already saved"] =
            "парите в профила минус бюджетираното и вече спестеното",
        ["A budget matures the saving into this month's plan; another bucket just shifts it across. The source bucket drops either way."] =
            "Бюджет превръща спестяването в план за този месец; друга каса просто го прехвърля. Касата източник намалява и в двата случая.",
        ["Contributions"] = "Вноски",
        ["Add expense"] = "Добави разход",
        ["All expenses"] = "Всички разходи",
        ["Savings buckets"] = "Спестовни каси",
        ["Add to savings"] = "Добави към спестявания",
        ["Budget savings"] = "Бюджетирай спестявания",
        ["Spend savings"] = "Похарчи спестявания",
        ["Records a real expense paid straight from this bucket (dated today)."] =
            "Записва реален разход, платен директно от тази каса (с днешна дата).",
        ["Previous day"] = "Предишен ден",
        ["Next day"] = "Следващ ден",
        ["All days"] = "Всички дни",

        // Common inline labels / empty states
        ["Amount"] = "Сума",
        ["Note (optional)"] = "Бележка (по избор)",
        ["no deposits yet"] = "още няма вноски",
        ["deposited"] = "внесени",
        ["No funds yet — add one."] = "Още няма сметки — добави.",
        ["No expenses yet."] = "Още няма разходи.",
        ["No members in this account yet."] = "Още няма членове в този профил.",
        ["No buckets yet — add one to start saving."] = "Още няма каси — добави, за да започнеш да спестяваш.",
        ["Deposit"] = "Внеси",
        ["Total saved:"] = "Общо спестено:",
        ["Total saved"] = "Общо спестено",
        ["this period:"] = "този период:",
        ["all periods:"] = "всички периоди:",
        ["all periods"] = "всички периоди",

        // Contributions
        ["Categories"] = "Категории",
        ["Add category"] = "Добави категория",
        ["Category"] = "Категория",
        ["Fund"] = "Сметка",
        ["Date"] = "Дата",
        ["Rename"] = "Преименувай",
        ["Edit deposit"] = "Редактирай вноска",
        ["Delete deposit?"] = "Изтриване на вноска?",
        ["New contribution category"] = "Нова категория вноски",
        ["Rename category"] = "Преименувай категория",
        ["Name"] = "Име",
        ["Remove category?"] = "Премахване на категория?",
        ["This removes the category permanently."] = "Това премахва категорията завинаги.",

        // Modal titles
        ["New account"] = "Нов профил",
        ["Rename account"] = "Преименувай профил",
        ["Remove this period?"] = "Премахване на този период?",
        ["Start next month"] = "Започни следващ месец",
        ["Edit expense"] = "Редактирай разход",
        ["Remove this expense?"] = "Премахване на този разход?",
        ["Remove this savings deposit?"] = "Премахване на тази спестовна вноска?",
        ["Edit transfer"] = "Редактирай прехвърляне",
        ["Remove this transfer?"] = "Премахване на това прехвърляне?",
        ["Edit period dates"] = "Редактирай датите на периода",
        ["Edit savings movement"] = "Редактирай спестовно движение",
        ["Remove this outgoing transfer?"] = "Премахване на това изходящо прехвърляне?",
        ["Invite to"] = "Покани в",
        ["Edit savings deposit"] = "Редактирай спестовна вноска",

        // Modal action buttons
        ["Cancel"] = "Отказ",
        ["Save"] = "Запази",
        ["Add"] = "Добави",
        ["Create"] = "Създай",
        ["Delete"] = "Изтрий",
        ["Remove"] = "Премахни",
        ["Close"] = "Затвори",
    };
}
