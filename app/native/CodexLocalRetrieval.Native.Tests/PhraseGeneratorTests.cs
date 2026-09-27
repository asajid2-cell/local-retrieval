using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The phrase a vetted chat answers to: [trait][color][fruit][food], four words drawn from four disjoint
// lists. These guard the two things the rest of the feature leans on - that a fresh phrase is always a
// well-formed one, and that IsGenerated tells the new scheme from the old handles it must not sweep.
[TestClass]
public sealed class PhraseGeneratorTests
{
    [TestMethod]
    public void Next_IsFourWords_EachFromItsOwnList()
    {
        var rng = new Random(20260927);
        for (var i = 0; i < 300; i++)
        {
            var phrase = PhraseGenerator.Next(rng);
            var parts = PhraseGenerator.Parts(phrase);
            Assert.AreEqual(4, parts.Length, $"a phrase is four words: {phrase}");
            Assert.IsTrue(PhraseGenerator.Traits.Contains(parts[0]), $"word 1 is a trait: {phrase}");
            Assert.IsTrue(PhraseGenerator.Colors.Contains(parts[1]), $"word 2 is a colour: {phrase}");
            Assert.IsTrue(PhraseGenerator.Fruits.Contains(parts[2]), $"word 3 is a fruit: {phrase}");
            Assert.IsTrue(PhraseGenerator.Foods.Contains(parts[3]), $"word 4 is a food: {phrase}");
            Assert.IsTrue(PhraseGenerator.IsGenerated(phrase), "a phrase the generator made is one it recognises");
        }
    }

    [TestMethod]
    public void Next_WithASeededRandom_IsDeterministic()
    {
        Assert.AreEqual(PhraseGenerator.Next(new Random(7)), PhraseGenerator.Next(new Random(7)));
        Assert.AreNotEqual(PhraseGenerator.Next(new Random(7)), PhraseGenerator.Next(new Random(8)),
            "two seeds should not agree on a 6.8M-wide draw");
    }

    [TestMethod]
    public void Parts_KeepsTheWordsAsTyped_AndCombineNormalisesThem()
    {
        // Parts feeds the four slot pickers, so it hands back the words themselves - it is Combine, the
        // one that builds a phrase, that lower-cases and drops blanks.
        CollectionAssert.AreEqual(new[] { "Brave", "Green", "Apple", "Pudding" },
            PhraseGenerator.Parts("  Brave   Green   Apple   Pudding "));
        Assert.AreEqual(0, PhraseGenerator.Parts(null).Length, "a missing phrase has no parts");
        Assert.AreEqual(0, PhraseGenerator.Parts("   ").Length);
        Assert.AreEqual(1, PhraseGenerator.Parts("petunia").Length, "a hand-typed phrase yields what it has");

        Assert.AreEqual("brave green pudding", PhraseGenerator.Combine("Brave", "Green", "", "Pudding"),
            "blanks drop out and the rest lowercases");
        Assert.AreEqual("", PhraseGenerator.Combine("", "", "", ""));
    }

    [TestMethod]
    public void IsGenerated_RejectsTheOldSchemeAndNearMisses()
    {
        Assert.IsFalse(PhraseGenerator.IsGenerated("petunia"), "a single-word handle is not a generated phrase");
        Assert.IsFalse(PhraseGenerator.IsGenerated(".mux"));
        Assert.IsFalse(PhraseGenerator.IsGenerated("brave green apple"), "three words is short");
        Assert.IsFalse(PhraseGenerator.IsGenerated("brave green apple gruel"), "the last word must be a food");
        Assert.IsFalse(PhraseGenerator.IsGenerated("brave green apple apple"), "the third word must be a fruit");
        Assert.IsFalse(PhraseGenerator.IsGenerated(null));
        Assert.IsTrue(PhraseGenerator.IsGenerated("Brave Green Apple Pudding"), "the check is case-insensitive");
    }

    [TestMethod]
    public void Lists_AreWellFormedAndTheCombinationCountIsLarge()
    {
        foreach (var (name, list) in new (string, IReadOnlyList<string>)[]
                 {
                     ("traits", PhraseGenerator.Traits), ("colors", PhraseGenerator.Colors),
                     ("fruits", PhraseGenerator.Fruits), ("foods", PhraseGenerator.Foods),
                 })
        {
            Assert.IsTrue(list.Count >= 40, $"{name}: enough words that a fresh draw rarely collides");
            foreach (var word in list)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(word), $"{name}: no blank entries");
                Assert.AreEqual(word.ToLowerInvariant(), word, $"{name}: entries are lower-case");
                Assert.AreEqual(1, PhraseGenerator.Parts(word).Length, $"{name}: '{word}' is one word");
            }
            Assert.AreEqual(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count(), $"{name}: no duplicates");
        }

        Assert.AreEqual(
            (long)PhraseGenerator.Traits.Count * PhraseGenerator.Colors.Count
            * PhraseGenerator.Fruits.Count * PhraseGenerator.Foods.Count,
            PhraseGenerator.CombinationCount);
        Assert.IsTrue(PhraseGenerator.CombinationCount > 1_000_000, "the space is big enough to stay a handle, not a collision");
    }
}
