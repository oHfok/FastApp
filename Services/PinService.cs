using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FastApp.Services
{
    // Parental-PIN hashing/verification, shared between the web dashboard's
    // /api/extend-limit endpoint and the WPF app's own native Extend Time dialog
    // (tray icon menu) — one implementation, so the two surfaces can never drift
    // out of sync with each other. Hashed + salted, never stored or returned in
    // plain text; this is friction, not a hardened security control.
    public static class PinService
    {
        public static (string Salt, string Hash) HashPin(string pin)
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
            byte[] hashBytes = SHA256.HashData(saltBytes.Concat(Encoding.UTF8.GetBytes(pin)).ToArray());
            return (Convert.ToBase64String(saltBytes), Convert.ToBase64String(hashBytes));
        }

        public static bool VerifyPin(string pin, string storedSalt, string storedHash)
        {
            if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(storedSalt) || string.IsNullOrEmpty(storedHash)) return false;
            byte[] saltBytes = Convert.FromBase64String(storedSalt);
            byte[] computedHash = SHA256.HashData(saltBytes.Concat(Encoding.UTF8.GetBytes(pin)).ToArray());
            return CryptographicOperations.FixedTimeEquals(computedHash, Convert.FromBase64String(storedHash));
        }

        public static string GetSettingValue(AppDbContext db, string key)
        {
            try
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT Value FROM AppSettings WHERE Key = @key";
                var param = command.CreateParameter();
                param.ParameterName = "@key";
                param.Value = key;
                command.Parameters.Add(param);
                db.Database.OpenConnection();
                using var result = command.ExecuteReader();
                return result.Read() ? result.GetString(0) : null;
            }
            catch
            {
                return null;
            }
        }

        public static (bool HasPin, string Salt, string Hash) GetPinInfo(AppDbContext db)
        {
            string hash = GetSettingValue(db, "ParentPinHash");
            string salt = GetSettingValue(db, "ParentPinSalt");
            return (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(salt), salt, hash);
        }
    }
}
