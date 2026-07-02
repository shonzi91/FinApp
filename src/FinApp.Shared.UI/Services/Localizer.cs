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

    /// <summary>Supported UI languages (code, display name, flag). Add a row here + a Bg-style map to add a language.</summary>
    public static readonly IReadOnlyList<(string Code, string Name, string Flag)> Languages =
    [
        ("en", "English", "🇬🇧"),
        ("bg", "Български", "🇧🇬"),
    ];

    private static bool IsSupported(string? code) => code is not null && Languages.Any(l => l.Code == code);

    public string Culture { get; private set; } = "en";

    /// <summary>The display name of the currently-selected language.</summary>
    public string CultureName => Languages.FirstOrDefault(l => l.Code == Culture).Name ?? Culture;

    /// <summary>The flag of the currently-selected language.</summary>
    public string CultureFlag => Languages.FirstOrDefault(l => l.Code == Culture).Flag ?? "🌐";
    public event Action? Changed;

    /// <summary>Load the saved language once at startup (call from the layout's OnInitializedAsync).</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var saved = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (IsSupported(saved) && saved != Culture)
            {
                Culture = saved;
                Changed?.Invoke();
            }
        }
        catch { /* storage unavailable — stay on English */ }
    }

    public async Task SetCultureAsync(string culture)
    {
        if (!IsSupported(culture) || culture == Culture) return;
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
        ["Profile settings"] = "Настройки на профила",
        ["Change password"] = "Смяна на парола",
        ["Current password"] = "Текуща парола",
        ["New password"] = "Нова парола",
        ["Confirm new password"] = "Потвърди новата парола",
        ["Password changed."] = "Паролата е сменена.",
        ["The new passwords don’t match."] = "Новите пароли не съвпадат.",
        ["Loading…"] = "Зареждане…",
        ["Saving…"] = "Запазване…",
        ["Dismiss"] = "Затвори",
        ["Couldn’t do that."] = "Неуспешно действие.",

        // Auth
        ["Private, shared budgeting. Sign in or create an account to begin."] =
            "Личен, споделен бюджет. Влез или създай профил, за да започнеш.",
        ["Track together, save together."] = "Следете заедно, спестявайте заедно.",
        ["Simple family goals, zero stress. Sign in or create an account to begin."] =
            "Прости семейни цели, нула стрес. Влез или създай профил, за да започнеш.",
        ["Sign in"] = "Вход",
        ["Create account"] = "Създай профил",
        ["or"] = "или",
        ["Continue with Google"] = "Влез с Google",
        ["Continue with Facebook"] = "Влез с Facebook",
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

        // Brand
        ["Budget like a budgie."] = "Бюджет, лек като перце.",

        // First run
        ["Welcome to TandemTab"] = "Добре дошъл в TandemTab",
        ["Let’s feather your nest. Create your first account to get started (e.g. Personal, Shared, Family)."] =
            "Да подредим гнездото. Създай първия си профил, за да започнеш (напр. Личен, Споделен, Семеен).",
        ["Ruffled feathers — overspent by"] = "Разрошени пера — преразход с",
        ["Expenses ate into your savings earmark. This will need to be covered next period (from a savings bucket or fresh contributions)."] =
            "Разходите изядоха заделените спестявания. Това трябва да се покрие следващия период (от спестовна каса или нови вноски).",
        ["Account name"] = "Име на профил",
        ["Currency"] = "Валута",
        ["It starts with a few starter categories and the current month’s period — you can change everything."] =
            "Започва с няколко начални категории и периода за текущия месец — всичко може да се променя.",

        // Tabs
        ["Account"] = "Профил",
        ["Budgets"] = "Бюджети",
        ["Expenses"] = "Разходи",
        ["Savings"] = "Спестявания",
        ["Insights"] = "Анализи",
        ["Overview"] = "Преглед",
        ["Home"] = "Начало",
        ["Spending"] = "Разходи",
        ["Setup"] = "Настройки",
        ["This month's budgets"] = "Бюджети за месеца",
        ["People"] = "Хора",
        ["Invite"] = "Покани",
        ["Privacy"] = "Поверителност",
        ["Terms"] = "Условия",
        ["you"] = "вие",
        ["owner"] = "собственик",
        ["Remove from account"] = "Премахни от акаунта",
        ["Leave account"] = "Напусни акаунта",
        ["Leave"] = "Напусни",
        ["Remove"] = "Премахни",
        ["New owner"] = "Нов собственик",
        ["Choose a member…"] = "Изберете член…",
        ["Archive account"] = "Архивирай акаунта",
        ["{0} will lose access to this account. Their recorded contributions and expenses stay."] = "{0} ще загуби достъп до този акаунт. Записаните вноски и разходи остават.",
        ["You're the only person here, so the account will be archived for 30 days. You can restore it from your profile before then; after that it's deleted."] = "Вие сте единственият тук, затова акаунтът ще бъде архивиран за 30 дни. Можете да го възстановите от профила си дотогава; след това се изтрива.",
        ["You own this account, so hand it to another member before you go."] = "Вие сте собственик на този акаунт, затова го предайте на друг член, преди да напуснете.",
        ["You'll lose access to this account. Your recorded contributions and expenses stay for the others."] = "Ще загубите достъп до този акаунт. Записаните ви вноски и разходи остават за останалите.",
        ["Archived accounts"] = "Архивирани акаунти",
        ["Archived accounts are deleted after 30 days. Restore one to bring it back."] = "Архивираните акаунти се изтриват след 30 дни. Възстановете, за да върнете акаунт.",
        ["Restore"] = "Възстанови",
        ["{0} days left"] = "остават {0} дни",
        ["Bank sync"] = "Банково синхронизиране",
        ["Bank"] = "Банка",
        ["Each account links its own bank."] = "Всеки акаунт свързва собствена банка.",
        ["No transactions waiting for this period. Hit Refresh to check for new ones."] = "Няма чакащи транзакции за този период. Натиснете Обнови, за да проверите за нови.",
        ["Refresh"] = "Обнови",
        ["Link Revolut"] = "Свържи Revolut",
        ["Link your Revolut account to pull transactions in automatically."] = "Свържете акаунта си в Revolut, за да се изтеглят транзакциите автоматично.",
        ["Reconnect"] = "Свържи отново",
        ["last synced"] = "последно синхр.",
        ["money in"] = "приход",
        ["Review each transaction, pick a category and fund, then add it as an expense."] = "Прегледайте всяка транзакция, изберете категория и фонд, след което я добавете като разход.",
        ["No transactions waiting. Hit Refresh to check for new ones."] = "Няма чакащи транзакции. Натиснете Обнови, за да проверите за нови.",
        ["Reopen the period to import transactions."] = "Отворете периода отново, за да импортирате транзакции.",
        ["Pick a category and fund first."] = "Първо изберете категория и фонд.",
        ["Disconnect"] = "Прекъсни",
        ["Always use this pick for this merchant"] = "Използвай този избор за този търговец занапред",
        ["Auto-fills {0} — click to forget"] = "Автоматично попълва {0} — щракнете, за да забравите",
        ["Map to… (optional)"] = "Свържи с… (по избор)",
        ["Money in"] = "Приходи",
        ["Auto-categorized merchants"] = "Автоматично категоризирани търговци",
        ["Imported transactions from these are filed here automatically. Unmapping only stops future auto-filing."] = "Импортираните транзакции от тях се завеждат тук автоматично. Премахването спира само бъдещото авт. завеждане.",
        ["Unmap"] = "Премахни",
        ["Pick something to map to first."] = "Първо изберете към какво да свържете.",
        ["Where your money is"] = "Къде са парите ви",
        ["Move money"] = "Премести пари",
        ["Income"] = "Приходи",
        ["free"] = "свободни",
        ["Money"] = "Пари",
        ["Actions"] = "Действия",
        ["Transfer"] = "Прехвърли",
        ["imported"] = "импортиран",
        ["of"] = "от",
        ["spent"] = "похарчени",
        ["Trends, savings rate & score"] = "Тенденции, норма на спестяване и оценка",

        // Overview tab
        ["Health score"] = "Оценка на здравето",
        ["Needs your attention"] = "Изисква внимание",
        ["All clear — no warnings this period. Nice work."] = "Всичко е наред — няма предупреждения този период. Браво!",
        ["Top spending"] = "Най-големи разходи",
        ["Log some income or expenses to see your account overview here."] =
            "Въведете приходи или разходи, за да видите преглед на профила тук.",
        ["Overspent budgets"] = "Преразходени бюджети",
        ["avg"] = "ср.",
        ["above average"] = "над средното",
        ["below average"] = "под средното",

        // Modal labels, titles, hints & tooltips (Session 12e translation pass)
        ["From"] = "От",
        ["Budget amount"] = "Сума на бюджета",
        ["Alert at %"] = "Предупреждение при %",
        ["Goal amount (optional)"] = "Целева сума (по избор)",
        ["Remove expense"] = "Премахни разхода",
        ["Add a fund"] = "Добави фонд",
        ["Delete account"] = "Изтрий профила",
        ["New savings bucket"] = "Нов спестовен джоб",
        ["Budget for this period (optional)"] = "Бюджет за този период (по избор)",
        ["Already saved (starting balance)"] = "Вече спестено (начален баланс)",
        ["Move balance to"] = "Премести баланса към",
        ["Notify on milestone"] = "Известявай при достигане на цел",
        ["Notify on every expense"] = "Известявай при всеки разход",
        ["Money you already had in this bucket before using FinApp. It counts toward the balance and goal, but not toward your savings rate."] =
            "Пари, които вече сте имали в този джоб преди да ползвате FinApp. Броят се към баланса и целта, но не и към нормата на спестяване.",
        ["Edit savings deposit —"] = "Редактирай спестяване —",
        ["Undo this savings movement?"] = "Да отмените ли това движение по спестяванията?",
        ["Remove those first."] = "Първо премахнете тях.",
        ["Can’t delete —"] = "Не може да се изтрие —",
        ["Can’t remove —"] = "Не може да се премахне —",
        ["Spend or move its savings first."] = "Първо похарчете или преместете спестяванията му.",
        ["This removes the empty bucket permanently."] = "Това премахва празния джоб завинаги.",
        ["This removes the fund permanently."] = "Това премахва фонда завинаги.",
        ["This fund has an opening balance. Move it to another fund, or remove it as-is (the balance is dropped)."] =
            "Този фонд има начален баланс. Преместете го към друг фонд или го премахнете така (балансът се губи).",
        ["Later periods shift to stay contiguous, keeping their own lengths."] =
            "Следващите периоди се изместват, за да останат последователни, запазвайки дължините си.",
        ["This permanently deletes the account and"] = "Това изтрива завинаги профила и",
        ["all its periods, budgets, expenses and savings"] = "всички негови периоди, бюджети, разходи и спестявания",
        ["This can't be undone."] = "Това не може да бъде отменено.",
        ["This deletes period"] = "Това изтрива период",
        ["and everything in it, then re-opens the previous period as active."] =
            "и всичко в него, след което активира отново предишния период.",
        ["Enter what each fund really holds now (previous closing balance:"] =
            "Въведете колко реално има всеки фонд сега (предишен краен баланс:",
        ["These become the new period's opening balances — that money carries over and is fully available to budget or save."] =
            "Те стават началните баланси на новия период — тези пари се прехвърлят и са напълно достъпни за бюджет или спестяване.",
        ["Enter the username of an existing FinApp user. They'll get a prompt to accept; once they do, they can edit everything except deleting the account."] =
            "Въведете потребителското име на съществуващ потребител. Той ще получи покана; след като я приеме, може да редактира всичко освен изтриването на профила.",
        ["Remove period"] = "Премахни периода",
        ["— don’t move —"] = "— не премествай —",
        ["Undo"] = "Отмени",
        ["Add a new account"] = "Добави нов профил",
        ["Invite a contributor"] = "Покани сътрудник",
        ["Remove this period and reopen the previous one"] = "Премахни този период и активирай предишния",
        ["Add a top-level category"] = "Добави основна категория",
        ["Add a savings bucket"] = "Добави спестовен джоб",
        ["Remove transfer"] = "Премахни прехвърлянето",
        ["Remove this transfer (does not reverse the deposit in the other account)"] =
            "Премахни това прехвърляне (не отменя депозита в другия профил)",
        ["You were invited to this account"] = "Бяхте поканени в този профил",
        ["Previous period"] = "Предишен период",
        ["Next period"] = "Следващ период",
        ["Sum of the period's opening fund values"] = "Сбор от началните стойности на фондовете за периода",
        ["Add bucket"] = "Добави джоб",
        ["Account actions"] = "Действия с профила",
        ["to"] = "към",

        // Insights tab — generated narrative, signals, trend, quick wins (format strings keep their {0}, {1}… slots)
        ["You're up {0} points from last month."] = "Нагоре с {0} точки спрямо миналия месец.",
        ["You're down {0} points from last month."] = "Надолу с {0} точки спрямо миналия месец.",
        ["Looking healthy"] = "Изглежда здравословно",
        ["Your habits are solid — saving steadily, spending within plan."] = "Навиците ви са стабилни — спестявате редовно и харчите по план.",
        ["Getting there"] = "На прав път",
        ["Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different."] =
            "Добра основа, но няколко навика ви дърпат надолу. Стегнете една област и следващият месец може да изглежда съвсем различно.",
        ["Needs attention"] = "Изисква внимание",
        ["A few things need a look this period — overspending or thin savings. Small fixes add up fast."] =
            "Няколко неща се нуждаят от внимание този период — преразход или слаби спестявания. Малките корекции бързо се натрупват.",
        ["Not enough history yet to spot a trend."] = "Все още няма достатъчно история за тенденция.",
        ["This month is right around your {0}-month average of {1}."] = "Този месец е около средното ви за {0} месеца от {1}.",
        ["This month is {0} above your {1}-month average of {2}."] = "Този месец е с {0} над средното ви за {1} месеца от {2}.",
        ["This month is {0} below your {1}-month average of {2}."] = "Този месец е с {0} под средното ви за {1} месеца от {2}.",
        ["{0} is running high"] = "{0} е завишен",
        ["You've spent {0} on {1} — {2} ({3}%) above your recent average of {4}."] =
            "Похарчили сте {0} за {1} — {2} ({3}%) над скорошното ви средно от {4}.",
        ["No savings set aside"] = "Няма заделени спестявания",
        ["You haven't moved anything into savings this period. Even a small amount keeps the habit alive."] =
            "Не сте заделили нищо за спестявания този период. Дори малка сума поддържа навика.",
        ["Savings on track"] = "Спестяванията са в час",
        ["You set aside {0} of what came in — at or above your {1} goal."] = "Заделили сте {0} от постъпленията — на или над целта ви от {1}.",
        ["{0} spend down"] = "По-малко разходи за {0}",
        ["{0} vs {1} last month. Keep it up."] = "{0} спрямо {1} миналия месец. Продължавайте така.",
        ["Days left in the period"] = "Оставащи дни в периода",
        ["You have {0} on hand with {1} days to go."] = "Разполагате с {0} при оставащи {1} дни.",
        ["{0}d left"] = "{0}д остават",
        ["Spending dipped into savings"] = "Разходите навлязоха в спестяванията",
        ["{0} of this period's spend isn't backed by fresh cash — it leans on your savings earmark."] =
            "{0} от разходите за този период не са покрити с нови пари — разчитат на заделените спестявания.",
        ["that category"] = "тази категория",
        ["Rein in {0}: you're {1} over budget this month."] = "Ограничете {0}: с {1} над бюджета този месец.",
        ["Set aside {0} more to hit your {1} savings goal."] = "Заделете още {0}, за да достигнете целта си за спестяване от {1}.",
        ["Give {0} a budget — you've spent {1} with no plan in place."] = "Задайте бюджет за {0} — похарчили сте {1} без план.",
        ["No contributions recorded this period, so there's no savings rate to measure yet."] =
            "Няма записани вноски за този период, така че още няма норма на спестяване.",
        ["You saved {0} this period — at or above your {1} goal. Keep that rhythm."] =
            "Спестихте {0} този период — на или над целта ви от {1}. Запазете темпото.",
        ["That's about {0} short of your goal this period."] = "Това е около {0} под целта ви за този период.",
        ["You saved {0} this period — better than nothing, but short of your {1} goal."] =
            "Спестихте {0} този период — по-добре от нищо, но под целта ви от {1}.",

        // Insights / financial-health tab
        ["Your score this period"] = "Вашата оценка за периода",
        ["out of 100"] = "от 100",
        ["At risk"] = "Рисково",
        ["Average"] = "Средно",
        ["Healthy"] = "Здравословно",
        ["This period's signals"] = "Сигнали за периода",
        ["Where it's going"] = "Накъде отиват парите",
        ["Savings rate"] = "Норма на спестяване",
        ["Target:"] = "Цел:",
        ["Goal"] = "Цел",
        ["Spending trend"] = "Тенденция на разходите",
        ["Outgoings"] = "Разходи",
        ["trending up"] = "във възход",
        ["trending down"] = "в спад",
        ["Quick wins"] = "Бързи победи",
        ["Once you've logged some income or expenses, your financial-health report shows up here."] =
            "След като въведете приходи или разходи, тук ще се появи отчетът за финансовото ви здраве.",
        ["Icon"] = "Икона",
        ["Auto (from name)"] = "Автоматично (по име)",
        ["Language"] = "Език",
        ["Profile picture"] = "Профилна снимка",
        ["Appearance"] = "Облик",
        ["Dark theme"] = "Тъмна тема",
        ["Sign-in"] = "Вход",
        ["You sign in with {0} — there's no password to manage."] = "Влизате чрез {0} — няма парола за управление.",
        ["Upload"] = "Качи",
        ["Stored on this device only."] = "Запазва се само на това устройство.",
        ["Sub-categories"] = "Подкатегории",
        ["Savings target (%)"] = "Цел за спестяване (%)",
        ["Edit account"] = "Редактирай профила",
        ["Your monthly savings goal — drives the Insights score."] =
            "Месечната ви цел за спестяване — определя оценката в Анализи.",
        ["can't be changed once an account exists."] = "не може да се променя след създаване на профила.",

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

        // Invitations
        ["You’re invited"] = "Имаш покана",
        ["pending invitation"] = "чакаща покана",
        ["pending invitations"] = "чакащи покани",
        ["invited you to"] = "те покани в",
        ["Accept"] = "Приеми",
        ["Decline"] = "Откажи",

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
        ["Nothing in the nest yet — add a deposit."] = "Още нищо в гнездото — добави вноска.",
        ["deposited"] = "внесени",
        ["No funds yet — add where your money lives."] = "Още няма сметки — добави къде стоят парите ти.",
        ["No expenses yet."] = "Още няма разходи.",
        ["Nothing’s perched here yet — add your first expense."] = "Тук още нищо не е кацнало — добави първия си разход.",
        ["No members in this account yet."] = "Още няма членове в този профил.",
        ["Your nest is empty — start a savings bucket and watch it grow."] = "Гнездото е празно — започни спестовна каса и я гледай как расте.",
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

        // Session 11 features
        ["On behalf of another account (settle later)"] = "От името на друг профил (уреди по-късно)",
        ["Settle onto another account"] = "Прехвърли към друг профил",
        ["Settle"] = "Прехвърли",
        ["Records this amount as an expense on the chosen account (in that fund and category) and reduces this expense by the same amount."] =
            "Записва сумата като разход в избрания профил (в този фонд и категория) и намалява този разход със същата сума.",
        ["Settled onto another account"] = "Прехвърлено към друг профил",
        ["Settled from another account"] = "Прехвърлено от друг профил",
        ["from"] = "от",
        ["Original:"] = "Първоначално:",
        ["Unsettle"] = "Отмени прехвърлянето",
        ["free to allocate"] = "налични за разпределяне",
        ["Over-allocated — allowed, just a heads-up."] = "Преразпределено — позволено е, само за сведение.",
        ["In this fund:"] = "В този фонд:",
        ["⚠ This dips into money earmarked for savings."] = "⚠ Това навлиза в средства, заделени за спестявания.",
        ["your cash that isn't already set aside for savings"] = "парите ти, които още не са заделени за спестявания",
        ["List"] = "Списък",
        ["Calendar"] = "Календар",
        ["Export to Excel"] = "Експорт в Excel",
        ["Manage category"] = "Управление на категорията",
        ["no budget"] = "без бюджет",
        ["No categories yet — add one."] = "Все още няма категории — добави.",
        ["No budgets yet — add a category with a budget (the ➕ above)."] = "Все още няма бюджети — добави категория с бюджет (➕ горе).",
        ["saved"] = "спестено",
        ["spent of"] = "похарчени от",
        ["No budget set for this category."] = "Няма зададен бюджет за тази категория.",
        ["Edit / budget"] = "Редакция / бюджет",
        ["Sub-category"] = "Подкатегория",
        ["No expenses in this category yet."] = "Все още няма разходи в тази категория.",
        ["your money minus savings — spending doesn't lower it"] = "парите ти минус спестяванията — похарченото не ги намалява",
        ["You have no other same-currency account to settle onto."] =
            "Нямаш друг профил в същата валута, към който да прехвърлиш.",
        ["Destination fund"] = "Целеви фонд",
        ["Adjust budgets to this period’s spending"] = "Коригирай бюджетите спрямо разходите за този период",
        ["Each budget moves halfway toward what was actually spent, rounded up to the nearest 10."] =
            "Всеки бюджет се приближава наполовина към реално похарченото, закръглено нагоре до 10.",
        ["Available to budget:"] = "Налично за бюджет:",
        ["the money in the account, minus what's budgeted elsewhere and already saved"] =
            "парите в профила, минус бюджетираното другаде и вече спестеното",
    };
}
