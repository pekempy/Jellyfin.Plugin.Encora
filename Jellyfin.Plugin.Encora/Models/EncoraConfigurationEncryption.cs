using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Encrypts/decrypts sensitive configuration values (API keys) for the on-disk backup file, using a
    /// random key generated once per installation and stored separately with restrictive permissions. This
    /// isn't protection against someone with full read access to the plugin's config folder - the key file
    /// lives right there too, same as Jellyfin's own plaintext XML config - it's aimed at the more common
    /// case of the backup file alone being copied, pasted, or shared without the key file alongside it.
    /// </summary>
    public static class EncoraConfigurationEncryption
    {
        private const string KeyFileName = "Jellyfin.Plugin.Encora.key";
        private const int KeySizeBytes = 32; // AES-256
        private const int NonceSizeBytes = 12; // AES-GCM standard nonce size
        private const int TagSizeBytes = 16; // AES-GCM standard tag size

        /// <summary>
        /// Encrypts <paramref name="plaintext"/> using the installation's key, generating a fresh key file
        /// if one doesn't exist yet.
        /// </summary>
        /// <param name="configFolderPath">The folder Jellyfin stores plugin configuration files in.</param>
        /// <param name="plaintext">The value to encrypt.</param>
        /// <returns>The encrypted value, base64-encoded (nonce + tag + ciphertext).</returns>
        public static string Encrypt(string configFolderPath, string plaintext)
        {
            var key = GetOrCreateKey(configFolderPath);
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

            var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSizeBytes];

            using var aesGcm = new AesGcm(key, TagSizeBytes);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            var combined = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, combined, 0, NonceSizeBytes);
            Buffer.BlockCopy(tag, 0, combined, NonceSizeBytes, TagSizeBytes);
            Buffer.BlockCopy(ciphertext, 0, combined, NonceSizeBytes + TagSizeBytes, ciphertext.Length);

            return Convert.ToBase64String(combined);
        }

        /// <summary>
        /// Decrypts a value previously produced by <see cref="Encrypt"/>.
        /// </summary>
        /// <param name="configFolderPath">The folder Jellyfin stores plugin configuration files in.</param>
        /// <param name="encrypted">The base64-encoded encrypted value.</param>
        /// <returns>The decrypted plaintext, or null if it couldn't be decrypted (e.g. missing/changed key).</returns>
        public static string? Decrypt(string configFolderPath, string encrypted)
        {
            byte[] combined;
            try
            {
                combined = Convert.FromBase64String(encrypted);
            }
            catch (FormatException)
            {
                return null;
            }

            if (combined.Length < NonceSizeBytes + TagSizeBytes)
            {
                return null;
            }

            var key = GetOrCreateKey(configFolderPath);
            var nonce = combined.AsSpan(0, NonceSizeBytes);
            var tag = combined.AsSpan(NonceSizeBytes, TagSizeBytes);
            var ciphertext = combined.AsSpan(NonceSizeBytes + TagSizeBytes);
            var plaintextBytes = new byte[ciphertext.Length];

            try
            {
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            }
            catch (CryptographicException)
            {
                return null;
            }

            return Encoding.UTF8.GetString(plaintextBytes);
        }

        private static byte[] GetOrCreateKey(string configFolderPath)
        {
            var path = Path.Combine(configFolderPath, KeyFileName);

            if (File.Exists(path))
            {
                return Convert.FromBase64String(File.ReadAllText(path).Trim());
            }

            var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
            Directory.CreateDirectory(configFolderPath);
            File.WriteAllText(path, Convert.ToBase64String(key));

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return key;
        }
    }
}
