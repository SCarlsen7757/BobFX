namespace BobFx.Core.Tests;

/// <summary>Hands out data folders for tests. They all live under one folder per test run, which is deleted
/// when the run ends, so settings files and key rings written by tests do not pile up in the temp folder.</summary>
internal static class TestPaths
{
    private static readonly string Root = CreateRoot();

    /// <summary>A path that does not exist yet; the settings store creates it when it first saves.</summary>
    public static string NewDataPath() => Path.Combine(Root, Guid.NewGuid().ToString("N"));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bobfx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A leftover folder must not fail the run. */ }
        };
        return root;
    }
}
