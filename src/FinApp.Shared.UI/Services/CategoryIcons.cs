using FinApp.Domain.Budgeting;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// The curated set of category icons (emoji) the user can pick from, plus a name-based guesser so
/// categories without an explicit icon (incl. pre-existing ones) still get something distinctive.
/// Presentation only — the chosen icon is stored on <see cref="Category.Icon"/> via the snapshot.
/// </summary>
public static class CategoryIcons
{
    /// <summary>Shown when a category has no icon and the name doesn't match anything.</summary>
    public const string Fallback = "🏷️";

    /// <summary>The ~32 predefined icons offered in the add/edit-category picker.</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "🍽️", "🛒", "🍔", "☕", "🍺", "🏠", "💡", "💧", "🔥", "🚗",
        "⛽", "🚌", "✈️", "🛍️", "👕", "💊", "🏥", "💪", "🎬", "🎮",
        "🎵", "📱", "💻", "🌐", "🎓", "📚", "🎁", "🐶", "👶", "💇",
        "🧾", "💰", "🏦", "🔧", "🌱", "🎨",
    ];

    // Ordered keyword → icon rules; first match wins. Lowercased "contains" matching.
    private static readonly (string[] Keywords, string Icon)[] Rules =
    [
        (["restaurant", "dining", "dine", "meal", "lunch", "dinner", "food", "eat"], "🍽️"),
        (["grocer", "supermarket"], "🛒"),
        (["fast", "burger", "takeaway", "takeout", "snack"], "🍔"),
        (["coffee", "cafe", "café"], "☕"),
        (["beer", "alcohol", "drink", "bar", "pub", "wine"], "🍺"),
        (["rent", "mortgage", "housing", "house", "home", "accommodation"], "🏠"),
        (["electric", "utilit", "bill", "power"], "💡"),
        (["water"], "💧"),
        (["heat", "heating"], "🔥"),
        (["fuel", "petrol", "diesel", "gasolin"], "⛽"),
        (["car", "auto", "vehicle"], "🚗"),
        (["bus", "train", "transit", "metro", "subway", "transport", "commut"], "🚌"),
        (["flight", "travel", "trip", "vacation", "holiday", "hotel"], "✈️"),
        (["cloth", "apparel", "shoe", "fashion"], "👕"),
        (["shop", "shopping"], "🛍️"),
        (["pharm", "medic", "medicine", "drug"], "💊"),
        (["health", "doctor", "dentist", "hospital", "clinic"], "🏥"),
        (["gym", "fitness", "sport", "workout"], "💪"),
        (["movie", "cinema", "entertain", "netflix"], "🎬"),
        (["game", "gaming", "playstation", "xbox"], "🎮"),
        (["music", "spotify", "concert"], "🎵"),
        (["phone", "mobile"], "📱"),
        (["tech", "computer", "software", "gadget", "electronic", "subscription"], "💻"),
        (["internet", "wifi", "web", "broadband"], "🌐"),
        (["school", "education", "tuition", "course", "class", "study"], "🎓"),
        (["book", "magazine", "news"], "📚"),
        (["gift", "present", "donation", "charity"], "🎁"),
        (["pet", "dog", "cat", "vet"], "🐶"),
        (["kid", "child", "baby", "family", "school"], "👶"),
        (["beauty", "hair", "salon", "cosmetic", "care", "grooming"], "💇"),
        (["tax", "fee", "fees", "charge"], "🧾"),
        (["saving", "save", "invest"], "💰"),
        (["bank", "loan", "debt", "credit", "insurance"], "🏦"),
        (["repair", "maintenance", "fix", "tool", "diy"], "🔧"),
        (["garden", "plant", "flower"], "🌱"),
        (["hobby", "hobbies", "craft", "art", "leisure", "fun"], "🎨"),
    ];

    /// <summary>The icon to display for a category: its explicit icon, else a guess from the name, else the fallback.</summary>
    public static string Effective(Category? category) =>
        category is null ? Fallback
        : !string.IsNullOrWhiteSpace(category.Icon) ? category.Icon!
        : Guess(category.Name);

    /// <summary>Best-effort icon for a category name (used when no icon is stored).</summary>
    public static string Guess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fallback;
        var n = name.ToLowerInvariant();
        foreach (var (keywords, icon) in Rules)
            foreach (var k in keywords)
                if (n.Contains(k, StringComparison.Ordinal))
                    return icon;
        return Fallback;
    }
}
