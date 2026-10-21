using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace SkyIslands.Core
{
    /// <summary>
    /// AES-256-CBC + HMAC-SHA256 (encrypt-then-MAC) JSON persistence under persistentDataPath.
    ///
    /// Keys are derived (PBKDF2) from the device id, which deters casual save editing but is
    /// not a secret on a rooted device. Integrity of the economy comes from server replay
    /// (sync_rolls); for stronger at-rest protection, swap DeriveKeys() for a key held in the
    /// Android Keystore / iOS Keychain via a native plugin.
    /// </summary>
    public static class SecureSave
    {
        const byte FormatVersion = 1;
        const int IvSize = 16;
        const int MacSize = 32;
        const int KdfIterations = 10_000;
        static readonly byte[] KdfSalt = Encoding.UTF8.GetBytes("SkyIslands.Voyage.SecureSave.v1");

        static byte[] _encKey;
        static byte[] _macKey;

        public static void Save<T>(string key, T value)
        {
            DeriveKeys();
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = _encKey;
            aes.GenerateIV();
            byte[] cipher;
            using (var enc = aes.CreateEncryptor()) cipher = enc.TransformFinalBlock(plain, 0, plain.Length);

            int bodyLen = 1 + IvSize + cipher.Length;
            var blob = new byte[bodyLen + MacSize];
            blob[0] = FormatVersion;
            Buffer.BlockCopy(aes.IV, 0, blob, 1, IvSize);
            Buffer.BlockCopy(cipher, 0, blob, 1 + IvSize, cipher.Length);
            using (var hmac = new HMACSHA256(_macKey))
                Buffer.BlockCopy(hmac.ComputeHash(blob, 0, bodyLen), 0, blob, bodyLen, MacSize);

            string path = PathFor(key);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Returns null when missing, tampered with, or unreadable.</summary>
        public static T Load<T>(string key) where T : class
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return null;
            try
            {
                DeriveKeys();
                byte[] blob = File.ReadAllBytes(path);
                int bodyLen = blob.Length - MacSize;
                if (bodyLen <= 1 + IvSize || blob[0] != FormatVersion) throw new CryptographicException("bad header");

                byte[] expected;
                using (var hmac = new HMACSHA256(_macKey)) expected = hmac.ComputeHash(blob, 0, bodyLen);
                if (!FixedTimeEquals(expected, blob, bodyLen)) throw new CryptographicException("MAC mismatch");

                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Key = _encKey;
                var iv = new byte[IvSize];
                Buffer.BlockCopy(blob, 1, iv, 0, IvSize);
                aes.IV = iv;
                byte[] plain;
                using (var dec = aes.CreateDecryptor())
                    plain = dec.TransformFinalBlock(blob, 1 + IvSize, bodyLen - 1 - IvSize);
                return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plain));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SecureSave] Discarding '{key}': {e.Message}");
                return null;
            }
        }

        public static void Delete(string key)
        {
            string path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }

        static string PathFor(string key) => Path.Combine(Application.persistentDataPath, key + ".sav");

        static void DeriveKeys()
        {
            if (_encKey != null) return;
            using var kdf = new Rfc2898DeriveBytes(
                SystemInfo.deviceUniqueIdentifier + "|" + Application.identifier, KdfSalt, KdfIterations,
                HashAlgorithmName.SHA256);
            _encKey = kdf.GetBytes(32);
            _macKey = kdf.GetBytes(32);
        }

        static bool FixedTimeEquals(byte[] expected, byte[] blob, int offset)
        {
            int diff = 0;
            for (int i = 0; i < MacSize; i++) diff |= expected[i] ^ blob[offset + i];
            return diff == 0;
        }
    }
}
