using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexLocalRetrieval.Core.Services;

// What a vetted chat answers to. A phrase is four words drawn from four different lists -
// [trait][color][fruit][food], so "brave green apple pudding" - which is the point: unlike an arbitrary
// handle it is easy to remember and easy to say out loud. The generator only supplies a starting point;
// the whole phrase and each of its four slots are the user's to choose, and a phrase can be saved as a
// reusable category ("apples" for mux work) instead of taking a fresh combination every time.
public static class PhraseGenerator
{
    public static readonly IReadOnlyList<string> Traits = new[]
    {
        "brave", "shy", "bold", "calm", "eager", "gentle", "witty", "quiet", "sharp", "swift",
        "lucky", "merry", "grim", "proud", "humble", "clever", "sleepy", "hungry", "sturdy", "nimble",
        "jolly", "solemn", "curious", "patient", "restless", "cheerful", "gloomy", "dapper", "rugged", "spry",
        "coy", "frank", "keen", "mild", "plucky", "quirky", "sage", "tame", "vivid", "zealous",
        "brisk", "courteous", "dainty", "earnest", "fearless", "gracious", "honest", "jaunty", "kindly", "lively",
        "mellow", "noble", "orderly", "pensive", "roguish", "spirited",
    };

    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "amber", "aqua", "azure", "beige", "black", "blue", "bronze", "brown", "chestnut", "cobalt",
        "copper", "coral", "cream", "crimson", "cyan", "emerald", "fawn", "fuchsia", "gold", "gray",
        "green", "indigo", "ivory", "jade", "khaki", "lavender", "lime", "magenta", "maroon", "mauve",
        "navy", "ochre", "olive", "orange", "peach", "pink", "plum", "purple", "red", "rose",
        "russet", "saffron", "scarlet", "silver", "tan", "teal", "turquoise", "violet", "walnut", "white",
        "yellow",
    };

    public static readonly IReadOnlyList<string> Fruits = new[]
    {
        "apple", "apricot", "avocado", "banana", "blackberry", "blueberry", "cherry", "coconut", "cranberry", "currant",
        "date", "dragonfruit", "durian", "elderberry", "fig", "grape", "grapefruit", "guava", "honeydew", "huckleberry",
        "jackfruit", "kiwi", "kumquat", "lemon", "lime", "lychee", "mandarin", "mango", "melon", "mulberry",
        "nectarine", "olive", "orange", "papaya", "passionfruit", "peach", "pear", "persimmon", "pineapple", "plum",
        "pomegranate", "quince", "raisin", "raspberry", "starfruit", "strawberry", "tangerine", "watermelon",
    };

    public static readonly IReadOnlyList<string> Foods = new[]
    {
        "bread", "pudding", "rice", "cake", "pie", "stew", "soup", "curry", "pasta", "noodle",
        "dumpling", "pancake", "waffle", "muffin", "biscuit", "scone", "bagel", "toast", "casserole", "risotto",
        "paella", "omelette", "frittata", "burger", "pizza", "taco", "burrito", "samosa", "kebab", "skewer",
        "salad", "coleslaw", "porridge", "granola", "custard", "trifle", "sorbet", "jelly", "jam", "marmalade",
        "pickle", "chutney", "gravy", "broth", "sauce", "cheese", "butter", "yogurt", "honey", "syrup",
    };

    // How many distinct phrases the lists can make. Big enough that a fresh combination is almost always
    // one nobody has, which is what keeps a phrase a usable handle rather than a collision.
    public static long CombinationCount =>
        (long)Traits.Count * Colors.Count * Fruits.Count * Foods.Count;

    public static string Combine(string trait, string color, string fruit, string food) =>
        string.Join(' ', new[] { trait, color, fruit, food }.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant()));

    // A fresh phrase. Pass a seeded Random in tests; the app takes the shared one.
    public static string Next(Random? random = null)
    {
        var rng = random ?? Random.Shared;
        return Combine(
            Traits[rng.Next(Traits.Count)],
            Colors[rng.Next(Colors.Count)],
            Fruits[rng.Next(Fruits.Count)],
            Foods[rng.Next(Foods.Count)]);
    }

    // The words a phrase is made of, so the four slot pickers can show and edit it piece by piece. A
    // hand-typed phrase that is not four words simply yields what it has.
    public static string[] Parts(string? phrase) =>
        (phrase ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // True when a phrase is one this generator could have produced: four words, each from its own slot's
    // list. This is how the retirement migration tells the new scheme from the old one, so it never sweeps
    // a phrase that was just issued by vetting.
    public static bool IsGenerated(string? phrase)
    {
        var parts = Parts(phrase);
        return parts.Length == 4
            && Traits.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            && Colors.Contains(parts[1], StringComparer.OrdinalIgnoreCase)
            && Fruits.Contains(parts[2], StringComparer.OrdinalIgnoreCase)
            && Foods.Contains(parts[3], StringComparer.OrdinalIgnoreCase);
    }
}
