using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Tenko.Native.Generated;

namespace Tenko.Native.Services
{
    public class StudentInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class StudentService
    {
        private const string EncryptedStudentsFileName = "students.enc";

        private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("TNKS");
        private const byte FileVersionAesGcm = 1;
        private const byte FileVersionAesCbcHmac = 2;
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int IvSize = 16;
        private const int HmacSize = 32;
        private const int KeySize = 32;
        private const int Pbkdf2Iterations = 200_000;

        private readonly StorageService _storage;
        private readonly Dictionary<ushort, StudentInfo> _studentMap = new();

        // 暗号化学生データを読み込み、検索用の辞書を構築する。
        public StudentService(StorageService storage)
        {
            _storage = storage;
            LoadStudents();
        }

        // students.enc を読み込み、CSV を復号して辞書へ取り込む。
        private void LoadStudents()
        {
            string encryptedPath = _storage.GetDataPath(EncryptedStudentsFileName);

            if (!_storage.Exists(encryptedPath))
            {
                Debug.WriteLine($"[StudentService] {EncryptedStudentsFileName} not found.");
                return;
            }

            try
            {
                string passphrase = EmbeddedStudentsPassphrase.GetPassphrase();
                if (string.IsNullOrWhiteSpace(passphrase))
                {
                    Debug.WriteLine("[StudentService] Embedded passphrase is empty. Student data will be empty.");
                    return;
                }

                byte[] encryptedBytes = _storage.ReadAllBytes(encryptedPath);
                string csvText = DecryptStudentsCsv(encryptedBytes, passphrase);
                var lines = csvText
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();

                if (lines.Length <= 1)
                {
                    Debug.WriteLine("[StudentService] Student data is empty.");
                    return;
                }

                string header = lines[0].Trim();
                if (!string.Equals(header, "student_number,name,code", StringComparison.Ordinal))
                {
                    Debug.WriteLine($"[StudentService] Invalid header: {header}");
                    return;
                }

                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length != 3)
                    {
                        Debug.WriteLine($"[StudentService] Skip malformed row: {line}");
                        continue;
                    }

                    if (ushort.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort studentNumber))
                    {
                        _studentMap[studentNumber] = new StudentInfo
                        {
                            Name = parts[1].Trim(),
                            Code = parts[2].Trim()
                        };
                    }
                    else
                    {
                        Debug.WriteLine($"[StudentService] Skip row with invalid student_number: {line}");
                    }
                }
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[StudentService] File read failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[StudentService] File access denied: {ex.Message}");
            }
            catch (CryptographicException ex)
            {
                Debug.WriteLine($"[StudentService] Decryption failed: {ex.Message}");
            }
            catch (InvalidDataException ex)
            {
                Debug.WriteLine($"[StudentService] Invalid encrypted format: {ex.Message}");
            }
        }

        // 暗号化データのヘッダを確認し、バージョンに応じて復号する。
        private static string DecryptStudentsCsv(byte[] encryptedBytes, string passphrase)
        {
            int minLength = FileMagic.Length + 1 + SaltSize;
            if (encryptedBytes.Length < minLength) throw new InvalidDataException("Encrypted data is too short.");

            for (int i = 0; i < FileMagic.Length; i++)
            {
                if (encryptedBytes[i] != FileMagic[i]) throw new InvalidDataException("Magic mismatch.");
            }

            byte version = encryptedBytes[FileMagic.Length];
            return version switch
            {
                FileVersionAesGcm => DecryptVersion1AesGcm(encryptedBytes, passphrase),
                FileVersionAesCbcHmac => DecryptVersion2AesCbcHmac(encryptedBytes, passphrase),
                _ => throw new InvalidDataException($"Unsupported version: {version}")
            };
        }

        // 旧形式(AES-GCM)の暗号データを復号する。
        private static string DecryptVersion1AesGcm(byte[] encryptedBytes, string passphrase)
        {
            int minLength = FileMagic.Length + 1 + SaltSize + NonceSize + TagSize;
            if (encryptedBytes.Length < minLength) throw new InvalidDataException("Encrypted v1 data is too short.");

            int offset = FileMagic.Length + 1;
            byte[] salt = encryptedBytes.AsSpan(offset, SaltSize).ToArray();
            offset += SaltSize;
            byte[] nonce = encryptedBytes.AsSpan(offset, NonceSize).ToArray();
            offset += NonceSize;
            byte[] tag = encryptedBytes.AsSpan(offset, TagSize).ToArray();
            offset += TagSize;
            byte[] ciphertext = encryptedBytes.AsSpan(offset).ToArray();
            if (ciphertext.Length == 0) throw new InvalidDataException("Encrypted payload is empty.");

            using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
            byte[] key = kdf.GetBytes(KeySize);
            byte[] plaintextBytes = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            return Encoding.UTF8.GetString(plaintextBytes).TrimStart('\uFEFF');
        }

        // 新形式(AES-CBC + HMAC)の暗号データを復号する。
        private static string DecryptVersion2AesCbcHmac(byte[] encryptedBytes, string passphrase)
        {
            int minLength = FileMagic.Length + 1 + SaltSize + IvSize + HmacSize + 1;
            if (encryptedBytes.Length < minLength) throw new InvalidDataException("Encrypted v2 data is too short.");

            int payloadLength = encryptedBytes.Length - HmacSize;
            byte[] payload = encryptedBytes.AsSpan(0, payloadLength).ToArray();
            byte[] mac = encryptedBytes.AsSpan(payloadLength, HmacSize).ToArray();

            int offset = FileMagic.Length + 1;
            byte[] salt = encryptedBytes.AsSpan(offset, SaltSize).ToArray();
            offset += SaltSize;
            byte[] iv = encryptedBytes.AsSpan(offset, IvSize).ToArray();
            offset += IvSize;
            byte[] ciphertext = encryptedBytes.AsSpan(offset, payloadLength - offset).ToArray();
            if (ciphertext.Length == 0) throw new InvalidDataException("Encrypted payload is empty.");

            using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
            byte[] encKey = kdf.GetBytes(KeySize);
            byte[] macKey = kdf.GetBytes(KeySize);

            using var hmac = new HMACSHA256(macKey);
            byte[] computedMac = hmac.ComputeHash(payload);
            if (!CryptographicOperations.FixedTimeEquals(computedMac, mac))
            {
                throw new CryptographicException("HMAC verification failed.");
            }

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = encKey;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            byte[] plaintextBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            return Encoding.UTF8.GetString(plaintextBytes).TrimStart('\uFEFF');
        }

        // 学籍番号下5桁から氏名とコードを返す。
        public (string Name, string Code) GetStudentInfo(ushort studentNumber)
        {
            if (_studentMap.TryGetValue(studentNumber, out var info))
            {
                return (info.Name, info.Code);
            }
            return (string.Empty, string.Empty);
        }
    }
}
