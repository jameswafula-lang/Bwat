/*
 * Bwat Server / PasswordHasher.cs
 *
 * Passwords are NEVER stored as plain text.
 * For every user we create a random "salt" (16 random bytes) and store
 *     PBKDF2(password, salt, 10 000 rounds)
 * PBKDF2 (Rfc2898DeriveBytes in .NET) is deliberately slow, so guessing
 * passwords from a stolen database is very expensive. The salt makes the
 * hash different even when two users choose the same password.
 */
using System;
using System.Security.Cryptography;

namespace Bwat.Server
{
    public static class PasswordHasher
    {
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const int Iterations = 10000;

        public static string NewSalt()
        {
            byte[] salt = new byte[SaltBytes];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                rng.GetBytes(salt);
            return Convert.ToBase64String(salt);
        }

        public static string Hash(string password, string salt)
        {
            using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, Convert.FromBase64String(salt), Iterations))
                return Convert.ToBase64String(pbkdf2.GetBytes(HashBytes));
        }

        /// <summary>Checks a password. Compares every byte so the time taken does not reveal how much matched.</summary>
        public static bool Verify(string password, string salt, string expectedHash)
        {
            string actual = Hash(password, salt);
            if (actual.Length != expectedHash.Length) return false;
            int diff = 0;
            for (int i = 0; i < actual.Length; i++)
                diff |= actual[i] ^ expectedHash[i];
            return diff == 0;
        }
    }
}
