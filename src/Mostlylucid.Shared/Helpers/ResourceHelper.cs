using System.Reflection;

namespace Mostlylucid.Shared.Helpers;

public static class ResourceHelper
{
    
    public static string GetMarkdownResource(string resourceName)
    {
        var assembly = Assembly.GetEntryAssembly();
        var resources = assembly.GetManifestResourceNames();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}