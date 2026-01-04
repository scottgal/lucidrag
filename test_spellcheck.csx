#!/usr/bin/env dotnet-script
#r "nuget: WeCantSpell.Hunspell, 5.0.0"
#r "nuget: Microsoft.Extensions.Logging.Console, 10.0.0"

using WeCantSpell.Hunspell;
using Microsoft.Extensions.Logging;

// Test dictionary loading and spell checking
var dictionaryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "lucidrag", "models", "dictionaries");

Console.WriteLine($"Dictionary path: {dictionaryPath}");

var affPath = Path.Combine(dictionaryPath, "en_US.aff");
var dicPath = Path.Combine(dictionaryPath, "en_US.dic");

if (!File.Exists(affPath) || !File.Exists(dicPath))
{
    Console.WriteLine("ERROR: Dictionary files not found!");
    Console.WriteLine($"  Looking for: {affPath}");
    Console.WriteLine($"  Looking for: {dicPath}");
    return;
}

Console.WriteLine("Loading dictionary...");
var dict = WordList.CreateFromFiles(dicPath, affPath);

if (dict == null)
{
    Console.WriteLine("ERROR: Failed to load dictionary!");
    return;
}

Console.WriteLine("Dictionary loaded successfully!\n");

// Test words from "Back Bf the net"
var testWords = new[] { "Back", "Bf", "the", "net" };

foreach (var word in testWords)
{
    var exact = dict.Check(word);
    var lower = dict.Check(word.ToLowerInvariant());

    Console.WriteLine($"Word: '{word}'");
    Console.WriteLine($"  Exact match: {exact}");
    Console.WriteLine($"  Lowercase match: {lower}");

    if (!exact && !lower)
    {
        var suggestions = dict.Suggest(word).Take(3).ToList();
        Console.WriteLine($"  Suggestions: {string.Join(", ", suggestions)}");
    }
    Console.WriteLine();
}
