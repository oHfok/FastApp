using FastApp.Services;
using Microsoft.Data.Sqlite;
using System.IO;

namespace FastApp.Tests;

/// <summary>
/// Each test gets its own empty data folder, standing in for "a PC", and puts the
/// shared one back afterwards.
/// </summary>
internal sealed class Pc : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "FastApp.Tests", "pc-" + Guid.NewGuid().ToString("N"), "FastAppData");
    public string Db => Path.Combine(Folder, "appmanager.db");

    public Pc() { Directory.CreateDirectory(Folder); Use(); }

    /// <summary>Makes this the PC the app is "running on", as if freshly launched.</summary>
    public void Use()
    {
        Environment.SetEnvironmentVariable("FASTAPP_DATA_DIR", Folder);
        DbEncryption.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FASTAPP_DATA_DIR", TestDatabase.Folder);
        DbEncryption.ResetForTests();
    }

    public static SqliteConnection Open(string path, string key)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = key ?? "", Pooling = false }.ToString());
        c.Open();
        return c;
    }

    public static object Run(string path, string key, string sql)
    {
        using var c = Open(path, key);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>An unencrypted database in WAL mode with some rows, as every install had before encryption.</summary>
    public static void WritePlain(string path, int rows = 500)
    {
        using var c = Open(path, null);
        foreach (var sql in new[]
        {
            "PRAGMA journal_mode=WAL;",
            "CREATE TABLE DailyLogs(Id INTEGER PRIMARY KEY, AppName TEXT, Minutes REAL);",
            "CREATE TABLE ManagedApps(Id INTEGER PRIMARY KEY, Name TEXT);",
            "CREATE TABLE AppSettings(Key TEXT PRIMARY KEY, Value TEXT);",
            $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<{rows}) INSERT INTO DailyLogs(AppName, Minutes) SELECT 'app' || i, i FROM n;",
            "INSERT INTO ManagedApps(Name) VALUES ('Discord'), ('Zen');"
        })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}

[Collection("Db")]
public class DbEncryptionTests
{
    [Fact]
    public void ANewInstallIsEncryptedFromTheStart()
    {
        using var pc = new Pc();
        using (var db = new AppDbContext()) db.Database.EnsureCreated();
        SqliteConnection.ClearAllPools();

        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.False(DbEncryption.IsPlain(pc.Db));
        Assert.ThrowsAny<SqliteException>(() => Pc.Run(pc.Db, null, "SELECT count(*) FROM sqlite_master"));
        Assert.ThrowsAny<SqliteException>(() => Pc.Run(pc.Db, new string('0', 64), "SELECT count(*) FROM sqlite_master"));
        Assert.True((long)Pc.Run(pc.Db, DbEncryption.Password, "SELECT count(*) FROM sqlite_master") > 0);
    }

    [Fact]
    public void AnExistingDatabaseIsEncryptedWithoutLosingARow()
    {
        using var pc = new Pc();
        Pc.WritePlain(pc.Db, rows: 2000);
        Assert.True(DbEncryption.IsPlain(pc.Db));
        pc.Use();   // "launch the new version"

        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.False(DbEncryption.IsPlain(pc.Db));
        Assert.Equal(2000L, Pc.Run(pc.Db, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
        Assert.Equal(2L, Pc.Run(pc.Db, DbEncryption.Password, "SELECT count(*) FROM ManagedApps"));
        Assert.Equal("ok", Pc.Run(pc.Db, DbEncryption.Password, "PRAGMA quick_check"));
        Assert.False(File.Exists(pc.Db + ".encrypting"));

        // The original is kept until a verified encrypted backup exists, then removed;
        // an older unencrypted backup lying in the folder is encrypted where it sits.
        string safetyNet = Path.Combine(pc.Folder, "appmanager.pre-encryption.db");
        string oldBackup = Path.Combine(pc.Folder, "appmanager.pre-autolaunch-migration.db");
        Assert.True(DbEncryption.IsPlain(safetyNet));
        Pc.WritePlain(oldBackup, rows: 7);
        SqliteConnection.ClearAllPools();
        File.Delete(oldBackup + "-wal"); File.Delete(oldBackup + "-shm");

        Assert.NotNull(AutoBackup.RunIfDue(new DateTime(2026, 2, 1)));

        Assert.False(File.Exists(safetyNet));
        Assert.False(DbEncryption.IsPlain(oldBackup));
        Assert.Equal(7L, Pc.Run(oldBackup, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
        Assert.All(AutoBackup.Existing(), f => Assert.False(DbEncryption.IsPlain(f.FullName)));
    }

    [Fact]
    public void RowsStillSittingInTheWriteAheadLogAreNotLeftBehind()
    {
        using var pc = new Pc();
        string live = Path.Combine(Path.GetTempPath(), "FastApp.Tests", $"live-{Guid.NewGuid():N}.db");
        Pc.WritePlain(live, rows: 100);

        // The state after FastApp was killed mid-write: rows committed to the -wal file
        // but never folded into the database. Copy the files while a connection holds them.
        using (var writer = Pc.Open(live, null))
        {
            foreach (var sql in new[] { "PRAGMA wal_autocheckpoint=0;", "INSERT INTO DailyLogs(AppName, Minutes) SELECT 'late' || Id, 1 FROM DailyLogs;" })
            {
                using var cmd = writer.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            Assert.True(new FileInfo(live + "-wal").Length > 0);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                using var from = new FileStream(live + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var to = File.Create(pc.Db + suffix);
                from.CopyTo(to);
            }
        }

        pc.Use();

        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(200L, Pc.Run(pc.Db, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
    }

    [Fact]
    public void ADatabaseAnotherProgramHasLockedIsLeftAloneNotMistakenForForeign()
    {
        using var pc = new Pc();
        Pc.WritePlain(pc.Db, rows: 10);
        SqliteConnection.ClearAllPools();

        using (new FileStream(pc.Db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            pc.Use();
            Assert.Equal(DbEncryption.DbState.Plain, DbEncryption.State);   // not NeedsRecoveryKey
        }

        pc.Use();                                                           // next launch: it goes through
        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(10L, Pc.Run(pc.Db, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
    }

    [Fact]
    public void TheRecoveryKeyIsTypableAndCatchesTypos()
    {
        using var pc = new Pc();
        string shown = DbEncryption.RecoveryKey;

        Assert.Matches("^([0-9A-HJKMNP-TV-Z]{5}-){10}[0-9A-HJKMNP-TV-Z]{5}$", shown);
        Assert.Equal(DbEncryption.Password, DbEncryption.Decode(shown));
        Assert.Equal(DbEncryption.Password, DbEncryption.Decode("  " + shown.ToLowerInvariant().Replace("-", " ") + "\n"));
        Assert.Equal(DbEncryption.Password, DbEncryption.Decode(shown.Replace('0', 'O').Replace('1', 'l')));

        char swapped = shown[7] == 'A' ? 'B' : 'A';
        Assert.Null(DbEncryption.Decode(shown[..7] + swapped + shown[8..]));
        Assert.Null(DbEncryption.Decode(shown[..^1]));
        Assert.Null(DbEncryption.Decode("not a key"));
        Assert.Null(DbEncryption.Decode(null));
    }

    [Fact]
    public void ADataFolderMovedToANewPcOpensWithTheRecoveryKeyOnly()
    {
        string recoveryKey;
        using var newPc = new Pc();
        using (var oldPc = new Pc())
        {
            Pc.WritePlain(oldPc.Db, rows: 300);
            oldPc.Use();
            recoveryKey = DbEncryption.RecoveryKey;
            SqliteConnection.ClearAllPools();
            File.Copy(oldPc.Db, newPc.Db);            // the database travels; Windows' copy of the key does not
        }

        newPc.Use();
        Assert.Equal(DbEncryption.DbState.NeedsRecoveryKey, DbEncryption.State);
        Assert.Null(DbEncryption.RecoveryKey);
        Assert.False(DbEncryption.TryUnlockWithRecoveryKey("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.Equal(DbEncryption.DbState.NeedsRecoveryKey, DbEncryption.State);

        Assert.True(DbEncryption.TryUnlockWithRecoveryKey(recoveryKey.ToLowerInvariant()));
        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(300L, Pc.Run(newPc.Db, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));

        newPc.Use();                                   // next launch: no question asked
        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(recoveryKey, DbEncryption.RecoveryKey);
    }

    [Fact]
    public void StartingFreshKeepsTheLockedDatabaseAside()
    {
        using var newPc = new Pc();
        using (var oldPc = new Pc())
        {
            Pc.WritePlain(oldPc.Db);
            oldPc.Use();
            _ = DbEncryption.State;
            SqliteConnection.ClearAllPools();
            File.Copy(oldPc.Db, newPc.Db);
        }
        newPc.Use();
        Assert.Equal(DbEncryption.DbState.NeedsRecoveryKey, DbEncryption.State);

        DbEncryption.StartFresh();

        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.False(File.Exists(newPc.Db));
        Assert.Single(Directory.GetFiles(newPc.Folder, "appmanager.locked-*.db"));
    }

    [Fact]
    public void RestoringABackupFromAnotherPcAdoptsItsKeyForEverythingHere()
    {
        string oldRecoveryKey, backupFromOldPc;
        using var newPc = new Pc();
        using (var oldPc = new Pc())
        {
            Pc.WritePlain(oldPc.Db, rows: 900);
            oldPc.Use();
            oldRecoveryKey = DbEncryption.RecoveryKey;
            backupFromOldPc = AutoBackup.RunIfDue(new DateTime(2026, 3, 1));
            Assert.NotNull(backupFromOldPc);
        }

        // A fresh install on the new PC, with a day of its own data and its own backup.
        newPc.Use();
        using (var db = new AppDbContext()) db.Database.EnsureCreated();
        string newPcOwnKey = DbEncryption.Password;
        string newPcBackup = AutoBackup.RunIfDue(new DateTime(2026, 3, 2));
        Assert.NotNull(newPcBackup);

        string staged = Path.Combine(Path.GetTempPath(), "FastApp.Tests", $"staged-{Guid.NewGuid():N}.db");
        File.Copy(backupFromOldPc, staged);

        Assert.Null(DbEncryption.KeyThatOpens(staged, null));                 // locked without the key
        Assert.Null(DbEncryption.KeyThatOpens(staged, DbEncryption.RecoveryKey));   // this PC's own key is the wrong one
        string stagedKey = DbEncryption.KeyThatOpens(staged, oldRecoveryKey);
        Assert.NotNull(stagedKey);

        DbEncryption.StageForRestore(staged, stagedKey);
        SqliteConnection.ClearAllPools();
        File.Copy(staged, newPc.Db, overwrite: true);                          // what PerformRestoreAsync does
        foreach (var s in new[] { "-wal", "-shm" }) File.Delete(newPc.Db + s);
        DbEncryption.CommitRestoredKey();

        newPc.Use();                                                           // the restart
        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(oldRecoveryKey, DbEncryption.RecoveryKey);                // one key follows the data
        Assert.NotEqual(newPcOwnKey, DbEncryption.Password);
        Assert.Equal(900L, Pc.Run(newPc.Db, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
        // The backup this PC made under its old key was re-encrypted, not orphaned.
        Assert.True(DbEncryption.Opens(newPcBackup, DbEncryption.Password));
        Assert.True(DbEncryption.RecoveryKeyShown);
    }

    [Fact]
    public void AnOldUnencryptedBackupIsEncryptedOnItsWayIn()
    {
        using var pc = new Pc();
        string staged = Path.Combine(Path.GetTempPath(), "FastApp.Tests", $"legacy-{Guid.NewGuid():N}.db");
        Pc.WritePlain(staged, rows: 40);
        SqliteConnection.ClearAllPools();

        DbEncryption.StageForRestore(staged, stagedKey: null);

        Assert.False(DbEncryption.IsPlain(staged));
        Assert.Equal(40L, Pc.Run(staged, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
        Assert.Equal(DbEncryption.Password, DbEncryption.KeyThatOpens(staged, null));
    }

    [Fact]
    public void ACrashHalfwayThroughTheFirstEncryptionLosesNothing()
    {
        using var pc = new Pc();
        // The state after "original moved aside" but before "encrypted copy moved in".
        Pc.WritePlain(Path.Combine(pc.Folder, "appmanager.pre-encryption.db"), rows: 120);
        SqliteConnection.ClearAllPools();
        Assert.False(File.Exists(pc.Db));

        pc.Use();

        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(120L, Pc.Run(pc.Db, DbEncryption.Password, "SELECT count(*) FROM DailyLogs"));
    }
}

[Collection("Db")]
public class RollbackFilterTests
{
    [Theory]
    [InlineData("6.2.1", false)]
    [InlineData("5.9.0", false)]
    [InlineData("6.6.9", false)]
    [InlineData("6.7.0", true)]
    [InlineData("v6.8.2", true)]
    [InlineData("nonsense", false)]
    public void OnlyVersionsThatUnderstandEncryptionAreOfferedForRollback(string version, bool offered)
    {
        using var pc = new Pc();
        Assert.Equal(DbEncryption.DbState.Encrypted, DbEncryption.State);
        Assert.Equal(offered, DbEncryption.CanBeOpenedBy(version));
    }
}
