using System.Text.Json;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

public static class ProfileIo
{
    public static void SaveProfiles(IEnumerable<DataProfile> profiles, string path)
    {
        var list = profiles.ToList();
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }

    public static List<DataProfile> LoadProfiles(string path)
    {
        var json = File.ReadAllText(path);
        var profiles = JsonSerializer.Deserialize<List<DataProfile>>(json) ?? new List<DataProfile>();
        return profiles;
    }
}
