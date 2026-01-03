namespace Mostlylucid.Shared.Helpers;

public static class LanguageConverter
{
    public static readonly Dictionary<string, string> LanguageMap = new Dictionary<string, string>
    {
        { "es", "Español (Spanish)" },
        { "fr", "Français (French)" },
        { "de", "Deutsch (German)" },
        { "it", "Italiano (Italian)" },
        { "zh", "中文 (Chinese Simplified)" },  // Chinese (Simplified)
        { "nl", "Nederlands (Dutch)" },
        { "hi", "हिन्दी (Hindi)"  },  // Hindi
        { "ar", "العربية (Arabic)" },  // Arabic
        { "uk", "Українська (Ukrainian)" },  // Ukrainian
        { "fi", "Suomi (Finnish)"  },  // Finnish
        { "sv", "Svenska (Swedish)" },  // Swedish
        { "en", "English" },
        { "el", "ελληνικά (Greek)" },
        { "pt", "Português (Portuguese)" },
        { "pl", "Polski (Polish)" }
    };
    
    private static readonly Dictionary<string, string> LanguageLocaleMap = new Dictionary<string, string>
    {
        { "es", "es-ES" },  // Spanish - Spain
        { "fr", "fr-FR" },  // French - France
        { "de", "de-DE" },  // German - Germany
        { "it", "it-IT" },  // Italian - Italy
        { "zh", "zh-CN" },  // Chinese (Simplified) - China
        { "nl", "nl-NL" },  // Dutch - Netherlands
        { "hi", "hi-IN" },  // Hindi - India
        { "ar", "ar-SA" },  // Arabic - Saudi Arabia
        { "uk", "uk-UA" },  // Ukrainian - Ukraine
        { "fi", "fi-FI" },  // Finnish - Finland
        { "sv", "sv-SE" },  // Swedish - Sweden
        { "en", "en-US" },  // English - United States
        { "el", "el-GR" },  // Greek - Greece
        { "pt", "pt-PT" },  // Portuguese - Portugal
        { "pl", "pl-PL" }   // Polish - Poland
    };

    private static readonly Dictionary<string, string> LanguageNameMap = new Dictionary<string, string>
    {
        { "es", "spanish" },
        { "fr", "french" },
        { "de", "german" },
        { "it", "italian" },
        { "zh", "chinese" },  // Chinese (Simplified)
        { "nl", "dutch" },
        { "hi", "hindi"  },  // Hindi
        { "ar", "arabic" },  // Arabic
        { "uk", "ukrainian" },  // Ukrainian
        { "fi", "finnish"  },  // Finnish
        { "sv", "swedish" },  // Swedish
        { "en", "english" },
        { "el", "greek" },
        { "pt", "portuguese" },
        { "pl", "polish" }
    };



    public static string ConvertCodeToLocale(this string code)
    {
        return LanguageLocaleMap.TryGetValue(code.ToLower(), out string locale) ? locale : "en-GB";
    }

    public static string ConvertCodeToLanguage(this string code)
    {
        return LanguageMap.TryGetValue(code.ToLower(), out string languageName) ? languageName : "Unknown Language";
    }
    
    public static string ConvertCodeToLanguageName(this string code)
    {
        return LanguageNameMap.TryGetValue(code.ToLower(), out string languageName) ? languageName : "Unknown Language";
    }
}