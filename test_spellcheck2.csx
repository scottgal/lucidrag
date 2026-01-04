#!/usr/bin/env dotnet-script
#r "nuget: WeCantSpell.Hunspell, 5.0.0"

using WeCantSpell.Hunspell;

var dictionaryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "lucidrag", "models", "dictionaries");

var affPath = Path.Combine(dictionaryPath, "en_US.aff");
var dicPath = Path.Combine(dictionaryPath, "en_US.dic");

var dict = WordList.CreateFromFiles(dicPath, affPath);

// Test clearly misspelled words
var testWords = new[] {
    ("Bf", "Should this be valid?"),
    ("BF", "Uppercase version"),
    ("bf", "Lowercase version"),
    ("Bff", "Best friends forever abbreviation"),
    ("asdfgh", "Clearly wrong"),
    ("teh", "Common typo for 'the'"),
    ("recieve", "Common misspelling of 'receive'")
};

foreach (var (word, note) in testWords)
{
    var isValid = dict.Check(word);
    Console.WriteLine($"{word,-15} Valid: {isValid,-5}  ({note})");

    if (isValid)
    {
        Console.WriteLine($"               ^^^ FOUND IN DICTIONARY!");
    }
    else
    {
        var suggestions = dict.Suggest(word).Take(5).ToList();
        if (suggestions.Any())
        {
            Console.WriteLine($"               Suggestions: {string.Join(", ", suggestions)}");
        }
    }
    Console.WriteLine();
}
