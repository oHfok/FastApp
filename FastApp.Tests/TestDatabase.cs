using System.IO;
using System.Runtime.CompilerServices;

namespace FastApp.Tests;

/// <summary>
/// Points the app's database at a throwaway folder before any test runs, so
/// nothing here can touch the real FastAppData\appmanager.db. Every test that
/// reaches AppDbContext must be in the "Db" collection: they share this one file.
/// </summary>
internal static class TestDatabase
{
    public static readonly string Folder =
        Path.Combine(Path.GetTempPath(), "FastApp.Tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("FASTAPP_DATA_DIR", Folder);
}

[CollectionDefinition("Db")]
public sealed class DbCollection { }
