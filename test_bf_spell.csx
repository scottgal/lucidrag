#!/usr/bin/env dotnet-script
#r "E:/source/lucidrag/src/Mostlylucid.DocSummarizer.Images/bin/Release/net10.0/Mostlylucid.DocSummarizer.Images.dll"
#r "nuget: WeCantSpell.Hunspell, 5.0.0"

using Mostlylucid.DocSummarizer.Images.Services.Ocr.PostProcessing;

var checker = new SpellChecker();

// Load en_US dictionary
var loaded = await checker.LoadDictionaryAsync("en_US");
Console.WriteLine($"Dictionary loaded: {loaded}\n");

if (!loaded)
{
    Console.WriteLine("ERROR: Dictionary not loaded!");
    return;
}

// Test the exact text from BackOfTheNet.gif
var testText = "Back Bf the net";

var result = checker.CheckTextQuality(testText, "en_US");

Console.WriteLine($"Text: '{testText}'");
Console.WriteLine($"Quality Score: {result.CorrectWordsRatio:P0} ({result.CorrectWords}/{result.TotalWords} words correct)");
Console.WriteLine($"Is Garbled: {result.IsGarbled}");
Console.WriteLine($"Recommend LLM Escalation: {result.RecommendLlmEscalation}");

if (result.MisspelledWords.Any())
{
    Console.WriteLine($"\nMisspelled Words: {string.Join(", ", result.MisspelledWords)}");

    foreach (var word in result.MisspelledWords)
    {
        if (result.Suggestions.ContainsKey(word))
        {
            Console.WriteLine($"  '{word}' -> {string.Join(", ", result.Suggestions[word])}");
        }
    }
}
else
{
    Console.WriteLine("\nNo misspelled words detected.");
}

// Expected behavior:
// - "Bf" should be flagged (OCR heuristic for suspicious 2-letter pattern)
// - Score should be 75% (3/4 words correct)
// - Should recommend LLM escalation (short text with errors)
Console.WriteLine("\n--- Expected Result ---");
Console.WriteLine("Score: 75% (3/4 words correct)");
Console.WriteLine("Misspelled: Bf");
Console.WriteLine("Recommend LLM Escalation: True");
