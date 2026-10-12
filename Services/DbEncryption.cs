using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FastApp.Services
{
    /// <summary>
    /// The database file is encrypted at rest (SQLite3 Multiple Ciphers, ChaCha20-Poly1305).
    ///
    /// Before this, appmanager.db opened in any SQLite viewer. Now it is unreadable
    /// without a 256-bit key that exists in two places only:
    ///
    ///   - on this PC, in db.key, encrypted by Windows for this account (DPAPI), so the
    ///     app opens the database without asking anyone anything;
    ///   - with the user, as the RECOVERY KEY -- the same key written in a form a person
    ///     can copy down. Windows' copy does not survive a reinstall, a new PC or a reset
    ///     profile, and every backup is encrypted with this key too, so the recovery key
    ///     is what makes the data portable. One key follows the data from PC to PC:
    ///     restoring a backup made elsewhere adopts that backup's key.
    ///
    /// What this protects against: other Windows accounts, a copied or synced folder, a
    /// stolen disk, a stray backup file. What it does not: a program running as this
    /// same user can ask Windows for the key exactly as FastApp does.
    ///
    /// Nothing here may touch AppDbContext -- that class asks this one for its password,
    /// and the two would recurse. Raw connections only.
    /// </summary>
    public static class DbEncryption
    {
        public enum DbState { Unknown, Encrypted, Plain, NeedsRecoveryKey }

        private static readonly object Gate = new();
        private static DbState _state = DbState.Unknown;
        private static string _password;

        private static string Folder => AppDbContext.GetDbFolder();
        private static string KeyPath => Path.Combine(Folder, "db.key");
        private static string PendingKeyPath => KeyPath + ".pending";
        private static string ShownMarker => Path.Combine(Folder, "recovery-key.shown");
        private static string PlainLeftover => Path.Combine(Folder, "appmanager.pre-encryption.db");

        /// <summary>The first release that can read an encrypted database.</summary>
        public static readonly Version FirstVersion = new(6, 7, 0);

        /// <summary>Whether a given FastApp version could open the database as it is now (used to filter rollbacks).</summary>
        public static bool CanBeOpenedBy(string version) =>
            State != DbState.Encrypted
            || (Version.TryParse((version ?? string.Empty).TrimStart('v'), out var v) && v >= FirstVersion);

        /// <summary>What the connection string needs. Null means "open without a key".</summary>
        public static string Password { get { EnsureReady(); return _password; } }

        public static DbState State { get { EnsureReady(); return _state; } }

        // ---------------------------------------------------------------- startup

        /// <summary>
        /// Works out where the database stands and, the first time, encrypts it. Safe to
        /// call from anywhere, any number of times; only the first call does anything.
        /// </summary>
        public static void EnsureReady()
        {
            if (_state != DbState.Unknown) return;
            lock (Gate)
            {
                if (_state != DbState.Unknown) return;
                try { Prepare(); }
                catch (Exception ex)
                {
                    // Never worse than before encryption existed: carry on unencrypted.
                    CrashLog.Log("Database encryption could not be set up", ex);
                    Set(DbState.Plain, null);
                }
            }
        }

        private static void Prepare()
        {
            string db = AppDbContext.GetDbPath();
            Directory.CreateDirectory(Folder);
            AppDbContext.AdoptLegacyDatabase(db);
            FinishInterruptedSwap(db);

            bool keyFileExists = File.Exists(KeyPath);
            string key = keyFileExists ? DashboardAccess.ReadProtected(KeyPath) : null;
            bool hasData = File.Exists(db) && new FileInfo(db).Length > 0;

            if (!hasData)
            {
                key ??= CreateKey();
                Set(key != null ? DbState.Encrypted : DbState.Plain, key);
            }
            else if (!CanReadHeader(db))
            {
                // Locked by another program, or unreadable for some other reason. Nothing
                // can be concluded from a file that will not open, least of all that it
                // needs a recovery key. Behave exactly as before encryption existed.
                CrashLog.Log("Database encryption skipped this launch", new IOException("The database file could not be read to check it."));
                Set(DbState.Plain, null);
            }
            else if (IsPlain(db))
            {
                key ??= CreateKey();
                bool done = key != null && EncryptLiveDatabase(db, key);
                Set(done ? DbState.Encrypted : DbState.Plain, done ? key : null);
            }
            else if (key != null && Opens(db, key))
            {
                Set(DbState.Encrypted, key);
            }
            else
            {
                // Encrypted, and this Windows account has no key that opens it: the folder
                // was moved from another PC, or the account was reset. Only the user can help.
                Set(DbState.NeedsRecoveryKey, null);
            }
        }

        private static void Set(DbState state, string password) { _password = password; _state = state; }

        /// <summary>Tests only: forget what was decided so the next call looks at the folder again.</summary>
        internal static void ResetForTests()
        {
            lock (Gate) { SqliteConnection.ClearAllPools(); Set(DbState.Unknown, null); }
        }

        // ---------------------------------------------------------------- the recovery key

        /// <summary>The key in the form shown to the user, or null when there is none to show.</summary>
        public static string RecoveryKey => State == DbState.Encrypted ? Encode(_password) : null;

        public static bool RecoveryKeyShown => File.Exists(ShownMarker);

        public static void MarkRecoveryKeyShown()
        {
            try { File.WriteAllText(ShownMarker, DateTime.Now.ToString("o")); } catch { }
        }

        /// <summary>
        /// For <see cref="DbState.NeedsRecoveryKey"/>: if what was typed opens the database,
        /// this PC keeps the key from now on.
        /// </summary>
        public static bool TryUnlockWithRecoveryKey(string typed)
        {
            lock (Gate)
            {
                string key = Decode(typed);
                if (key == null || !Opens(AppDbContext.GetDbPath(), key)) return false;
                if (!StoreKey(key)) return false;
                Set(DbState.Encrypted, key);
                MarkRecoveryKeyShown();   // they plainly have it
                return true;
            }
        }

        /// <summary>
        /// For <see cref="DbState.NeedsRecoveryKey"/> when the key is gone: sets the locked
        /// database aside (never deletes it) and starts an empty one under a new key.
        /// </summary>
        public static void StartFresh()
        {
            lock (Gate)
            {
                SqliteConnection.ClearAllPools();
                string db = AppDbContext.GetDbPath();
                string aside = Path.Combine(Folder, $"appmanager.locked-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                if (File.Exists(db)) File.Move(db, aside);
                foreach (var suffix in new[] { "-wal", "-shm" })
                    try { if (File.Exists(db + suffix)) File.Delete(db + suffix); } catch { }
                try { if (File.Exists(ShownMarker)) File.Delete(ShownMarker); } catch { }

                string key = CreateKey();
                Set(key != null ? DbState.Encrypted : DbState.Plain, key);
            }
        }

        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford: no I, L, O or U

        /// <summary>64 hex characters to eleven groups of five, with a two-byte check so a typo is caught.</summary>
        internal static string Encode(string hexKey)
        {
            byte[] key = Convert.FromHexString(hexKey);
            byte[] all = key.Concat(SHA256.HashData(key).Take(2)).ToArray();

            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (byte b in all)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
                buffer &= (1 << bits) - 1;
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);

            return string.Join("-", Enumerable.Range(0, (sb.Length + 4) / 5)
                .Select(i => sb.ToString(i * 5, Math.Min(5, sb.Length - i * 5))));
        }

        /// <summary>The hex key for what a person typed, forgiving case, spacing and O/0, I/L/1 mix-ups. Null if it is not a valid key.</summary>
        internal static string Decode(string typed)
        {
            if (string.IsNullOrWhiteSpace(typed)) return null;
            var symbols = typed.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1')
                .Where(c => Alphabet.IndexOf(c) >= 0).ToArray();
            if (symbols.Length != 55) return null;

            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (char c in symbols)
            {
                buffer = (buffer << 5) | Alphabet.IndexOf(c); bits += 5;
                if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
                buffer &= (1 << bits) - 1;
            }
            if (bytes.Count < 34) return null;

            byte[] key = bytes.Take(32).ToArray();
            return SHA256.HashData(key).Take(2).SequenceEqual(bytes.Skip(32).Take(2)) ? Convert.ToHexString(key) : null;
        }

        // ---------------------------------------------------------------- restoring a backup

        /// <summary>
        /// The key that opens an encrypted backup: this PC's own, or the one behind the
        /// recovery key the user typed. Null if neither does.
        /// </summary>
        public static string KeyThatOpens(string backupPath, string typedRecoveryKey)
        {
            string mine = Password;
            if (mine != null && Opens(backupPath, mine)) return mine;
            string theirs = Decode(typedRecoveryKey);
            return theirs != null && Opens(backupPath, theirs) ? theirs : null;
        }

        /// <summary>
        /// Makes a validated, staged backup ready to become the live database.
        /// An old unencrypted backup is encrypted with this PC's key. A backup under
        /// another key is left as it is and that key is queued, to be adopted at the
        /// moment the file is swapped in (<see cref="CommitRestoredKey"/>).
        /// </summary>
        public static void StageForRestore(string stagedPath, string stagedKey)
        {
            string mine = Password;
            try { if (File.Exists(PendingKeyPath)) File.Delete(PendingKeyPath); } catch { }

            if (stagedKey == null)
            {
                if (mine == null) return;                       // encryption is off on this PC
                string encrypted = stagedPath + ".enc";
                EncryptCopy(stagedPath, null, encrypted, mine);
                File.Move(encrypted, stagedPath, overwrite: true);
            }
            else if (stagedKey != mine)
            {
                DashboardAccess.WriteProtected(PendingKeyPath, stagedKey);
                if (DashboardAccess.ReadProtected(PendingKeyPath) != stagedKey)
                    throw new IOException("Could not save the backup's key, so the restore was not started.");
            }
        }

        /// <summary>
        /// Called right after a restored file has replaced the live database. If it came
        /// with its own key, that key becomes this PC's, and the copies FastApp already
        /// holds here are re-encrypted with it so one recovery key opens everything.
        /// </summary>
        public static void CommitRestoredKey()
        {
            lock (Gate)
            {
                string adopted = File.Exists(PendingKeyPath) ? DashboardAccess.ReadProtected(PendingKeyPath) : null;
                if (adopted == null) return;

                string previous = _password;
                SqliteConnection.ClearAllPools();
                foreach (var file in OtherCopies())
                    try { if (!IsPlain(file) && Opens(file, previous)) Rekey(file, previous, adopted); } catch { }

                File.Move(PendingKeyPath, KeyPath, overwrite: true);
                Set(DbState.Encrypted, adopted);
                MarkRecoveryKeyShown();   // the user typed it to get here
            }
        }

        // ---------------------------------------------------------------- unencrypted leftovers

        /// <summary>
        /// Once a verified encrypted backup exists, the unencrypted copies still lying
        /// around are dealt with: the one kept as a safety net during the first
        /// encryption is deleted, and older backups are encrypted where they sit.
        /// A copy that cannot be read (already damaged) is left exactly as it is.
        /// </summary>
        public static void CleanUpUnencryptedCopies()
        {
            string key = Password;
            if (State != DbState.Encrypted || key == null) return;

            try { if (File.Exists(PlainLeftover)) File.Delete(PlainLeftover); } catch { }

            int done = 0, left = 0;
            foreach (var file in OtherCopies())
            {
                try
                {
                    // A copy with its own write-ahead log is a forensic snapshot: do not touch it.
                    if (!IsPlain(file) || File.Exists(file + "-wal")) continue;
                    string encrypted = file + ".enc";
                    EncryptCopy(file, null, encrypted, key);
                    File.Move(encrypted, file, overwrite: true);
                    done++;
                }
                catch
                {
                    try { File.Delete(file + ".enc"); } catch { }
                    left++;
                }
            }
            if (done + left > 0)
                PerfLog.Event("encryption", $"older copies encrypted={done} left-unreadable={left}");
        }

        /// <summary>Every database copy FastApp keeps besides the live one.</summary>
        private static IEnumerable<string> OtherCopies()
        {
            var found = new List<string>();
            void Add(string dir, string pattern)
            {
                try { if (Directory.Exists(dir)) found.AddRange(Directory.GetFiles(dir, pattern)); } catch { }
            }

            Add(Folder, "appmanager.pre-*.db");
            Add(AutoBackup.Folder, "*.db");
            try
            {
                foreach (var dir in Directory.GetDirectories(Folder))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("pre-restore", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("corrupt-backup", StringComparison.OrdinalIgnoreCase))
                        Add(dir, "*.db");
                }
            }
            catch { }
            return found.Where(f => !string.Equals(f, PlainLeftover, StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------- first-time encryption of the live database

        /// <summary>
        /// Encrypts a copy, proves the copy is complete, and only then swaps it in. The
        /// original is kept (as appmanager.pre-encryption.db) until a later backup has
        /// verified the encrypted database end to end.
        /// </summary>
        private static bool EncryptLiveDatabase(string db, string key)
        {
            string temp = db + ".encrypting";
            try
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(temp)) File.Delete(temp);
                EncryptCopy(db, null, temp, key);

                File.Move(db, PlainLeftover, overwrite: true);
                // The write-ahead log belonged to the old file; replaying it into a
                // different database is its own corruption.
                foreach (var suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(db + suffix)) File.Delete(db + suffix);
                File.Move(temp, db);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                // If the original was already moved aside, FinishInterruptedSwap puts it back next launch;
                // do it now so this session still has a database.
                try { if (!File.Exists(db) && File.Exists(PlainLeftover)) File.Move(PlainLeftover, db); } catch { }
                CrashLog.Log("Database encryption postponed (the database was left as it was)", ex);
                return false;
            }
        }

        /// <summary>If a previous launch died between the two renames above, finish or undo it.</summary>
        private static void FinishInterruptedSwap(string db)
        {
            string temp = db + ".encrypting";
            if (File.Exists(db))
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return;
            }
            if (!File.Exists(PlainLeftover)) return;

            string key = File.Exists(KeyPath) ? DashboardAccess.ReadProtected(KeyPath) : null;
            if (File.Exists(temp) && key != null && Opens(temp, key)) File.Move(temp, db);
            else File.Move(PlainLeftover, db);
        }

        // ---------------------------------------------------------------- building blocks

        private static bool CanReadHeader(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return fs.Read(new byte[16], 0, 16) == 16;
            }
            catch { return false; }
        }

        internal static bool IsPlain(string path)
        {
            try
            {
                var header = new byte[16];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return fs.Read(header, 0, 16) == 16 && Encoding.ASCII.GetString(header, 0, 15) == "SQLite format 3";
            }
            catch { return false; }
        }

        internal static bool Opens(string path, string key)
        {
            try
            {
                using var connection = Open(path, key, readOnly: true);
                Scalar(connection, "SELECT count(*) FROM sqlite_master;");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Writes a copy of <paramref name="source"/> to <paramref name="destination"/> under
        /// <paramref name="newKey"/> and throws unless the copy is intact and holds exactly the
        /// same rows, table for table. The source is never modified.
        /// </summary>
        internal static void EncryptCopy(string source, string sourceKey, string destination, string newKey)
        {
            if (File.Exists(destination)) File.Delete(destination);

            Dictionary<string, long> before;
            using (var connection = Open(source, sourceKey, readOnly: false))
            {
                try { Scalar(connection, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch { }
                before = RowCounts(connection);
                Scalar(connection, $"VACUUM INTO '{destination.Replace("'", "''")}';");
            }

            Rekey(destination, sourceKey, newKey);

            using (var connection = Open(destination, newKey, readOnly: true))
            {
                if (!string.Equals(Scalar(connection, "PRAGMA quick_check;") as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The encrypted copy failed its integrity check.");
                var after = RowCounts(connection);
                if (after.Count != before.Count || before.Any(t => !after.TryGetValue(t.Key, out long n) || n != t.Value))
                    throw new InvalidDataException("The encrypted copy does not hold the same rows as the original.");
            }
        }

        internal static void Rekey(string path, string oldKey, string newKey)
        {
            using var connection = Open(path, oldKey, readOnly: false);
            using var quote = connection.CreateCommand();
            quote.CommandText = "SELECT quote($key);";
            quote.Parameters.AddWithValue("$key", newKey);
            Scalar(connection, $"PRAGMA rekey = {quote.ExecuteScalar()};");
        }

        private static Dictionary<string, long> RowCounts(SqliteConnection connection)
        {
            var tables = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
                using var reader = list.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
            }
            return tables.ToDictionary(t => t, t => (long)Scalar(connection, $"SELECT count(*) FROM \"{t.Replace("\"", "\"\"")}\";"));
        }

        // Pooling off throughout: these files get renamed and replaced, which a pooled handle would block.
        private static SqliteConnection Open(string path, string key, bool readOnly)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Password = key ?? string.Empty,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        private static object Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        /// <summary>A new random key, saved for this Windows account. Null if it could not be saved (then nothing is encrypted).</summary>
        private static string CreateKey()
        {
            string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            return StoreKey(key) ? key : null;
        }

        private static bool StoreKey(string key)
        {
            try
            {
                // Never silently overwrite a key file this account cannot read: it may be the
                // only trace of another PC's key. Set it aside instead.
                if (File.Exists(KeyPath) && DashboardAccess.ReadProtected(KeyPath) == null)
                    File.Move(KeyPath, KeyPath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}");
                DashboardAccess.WriteProtected(KeyPath, key);
                return DashboardAccess.ReadProtected(KeyPath) == key;
            }
            catch { return false; }
        }
    }
}
