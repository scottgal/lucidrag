using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;

namespace Mostlylucid.Shared.Helpers;

public static partial class StringHelpers
{
    public static string TruncateAtWord(this string value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= length)
            return value;

        int endIndex = value.LastIndexOf(' ', length);
        return endIndex > 0 ? value.Substring(0, endIndex) : value.Substring(0, length);
    }
    
     public  static string ToGuid(this string  name)
    {
        var buf = Encoding.UTF8.GetBytes(name);
        var guid = XxHash128.Hash(buf);
        var guidS =  string.Format("{0:X2}{1:X2}{2:X2}{3:X2}-{4:X2}{5:X2}-{6:X2}{7:X2}-{8:X2}{9:X2}-{10:X2}{11:X2}{12:X2}{13:X2}{14:X2}{15:X2}", 
            guid[0], guid[1], guid[2], guid[3], guid[4], guid[5], guid[6], guid[7], guid[8], guid[9], guid[10], guid[11], guid[12], guid[13], guid[14], guid[15]);
        return guidS.ToLowerInvariant();
    }
     
     
    public static string ContentHash(this string content)
    {
        var buf = Encoding.UTF8.GetBytes(content);
        var hash = XxHash128.Hash(buf);
        return BitConverter.ToString(hash).Replace("-", "");
    }
    
    [System.Text.RegularExpressions.GeneratedRegex(@"\b\w+\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex WordCountRegex();
    public  static int WordCount(this string text) => WordCountRegex().Matches(text).Count;
    
    public static string GetOrdinal(this int number)
    {
        if (number <= 0) return number.ToString();

        // Handle special cases for 11, 12, 13
        if (number % 100 >= 11 && number % 100 <= 13)
            return number + "th";

        switch (number % 10)
        {
            case 1: return number + "st";
            case 2: return number + "nd";
            case 3: return number + "rd";
            default: return number + "th";
        }
    }
}