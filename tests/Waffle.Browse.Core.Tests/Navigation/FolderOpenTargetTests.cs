using Waffle.Browse.Core.Navigation;

namespace Waffle.Browse.Core.Tests.Navigation;

internal static class FolderOpenTargetTests
{
    public static void FolderOpenTargetUsesExplorerWithPathArgument()
    {
        const string path = @"C:\Users\Test User\Documents";

        var target = FolderOpenTarget.ForPath(path);

        TestAssert.Equal("explorer.exe", target.FileName, "Folders should open with Windows File Explorer");
        TestAssert.Equal(1, target.Arguments.Count, "Folder open should pass one path argument");
        TestAssert.Equal(path, target.Arguments[0], "Folder path argument should be preserved without manual quoting");
    }
}
