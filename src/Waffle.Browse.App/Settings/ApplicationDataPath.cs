using System.IO;

namespace Waffle.Browse.App.Settings;

internal static class ApplicationDataPath
{
    private const string CurrentDirectoryName = "Waffle Browse";
    private const string LegacyDirectoryName = "F-Finder";

    public static string Resolve()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Resolve(localApplicationData);
    }

    internal static string Resolve(string localApplicationData)
    {
        var currentPath = Path.Combine(localApplicationData, CurrentDirectoryName);
        var legacyPath = Path.Combine(localApplicationData, LegacyDirectoryName);

        if (Directory.Exists(currentPath) || !Directory.Exists(legacyPath))
        {
            return currentPath;
        }

        try
        {
            Directory.Move(legacyPath, currentPath);
            return currentPath;
        }
        catch (IOException)
        {
            return legacyPath;
        }
        catch (UnauthorizedAccessException)
        {
            return legacyPath;
        }
    }
}
