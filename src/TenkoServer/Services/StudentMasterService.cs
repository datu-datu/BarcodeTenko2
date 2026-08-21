using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Tenko.Native.Generated;
using TenkoServer.Models;
using TenkoServer.Models.DTOs;

namespace TenkoServer.Services
{
    public class StudentInfo
    {
        public ushort StudentNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    public interface IStudentMasterService
    {
        IReadOnlyDictionary<ushort, StudentInfo> GetAllStudents();
        (string Name, string Code, string Email) GetStudentInfo(ushort studentNumber);
        List<UnverifiedStudentDto> GetUnverifiedStudents(IEnumerable<ushort> scannedStudentNumbers);
        string FormatStudentEmail(ushort studentNumber);
    }

    public class StudentMasterService : IStudentMasterService
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

        private readonly TenkoServerOptions _options;
        private readonly ILogger<StudentMasterService> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly Dictionary<ushort, StudentInfo> _studentMap = new();

        public StudentMasterService(
            IOptions<TenkoServerOptions> options,
            ILogger<StudentMasterService> logger,
            IWebHostEnvironment env)
        {
            _options = options.Value;
            _logger = logger;
            _env = env;

            LoadStudents();
        }

        public string FormatStudentEmail(ushort studentNumber)
        {
            string domain = string.IsNullOrWhiteSpace(_options.EmailDomain) ? "tokyo.kosen-ac.jp" : _options.EmailDomain;
            return $"s{studentNumber:D5}@{domain}";
        }

        public IReadOnlyDictionary<ushort, StudentInfo> GetAllStudents() => _studentMap;

        public (string Name, string Code, string Email) GetStudentInfo(ushort studentNumber)
        {
            if (_studentMap.TryGetValue(studentNumber, out var info))
            {
                return (info.Name, info.Code, info.Email);
            }
            return (string.Empty, string.Empty, FormatStudentEmail(studentNumber));
        }

        public List<UnverifiedStudentDto> GetUnverifiedStudents(IEnumerable<ushort> scannedStudentNumbers)
        {
            var scannedSet = new HashSet<ushort>(scannedStudentNumbers);
            return _studentMap.Values
                .Where(s => !scannedSet.Contains(s.StudentNumber))
                .OrderBy(s => s.Code)
                .ThenBy(s => s.StudentNumber)
                .Select(s => new UnverifiedStudentDto
                {
                    StudentNumber = s.StudentNumber,
                    Name = s.Name,
                    Code = s.Code,
                    Email = s.Email
                })
                .ToList();
        }

        private void LoadStudents()
        {
            // 探索パス: 実行ディレクトリ/data/students.enc, WebRootPath/data/students.enc, コンテンツルート/data/students.enc
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", EncryptedStudentsFileName),
                Path.Combine(_env.ContentRootPath, "data", EncryptedStudentsFileName),
                Path.Combine(_env.ContentRootPath, "..", "..", "data", EncryptedStudentsFileName)
            };

            string? targetPath = candidates.FirstOrDefault(File.Exists);
            if (targetPath == null)
            {
                _logger.LogWarning("students.enc not found in search paths. Master student list will be empty.");
                return;
            }

            try
            {
                string passphrase = EmbeddedStudentsPassphrase.GetPassphrase();
                if (string.IsNullOrWhiteSpace(passphrase))
                {
                    _logger.LogWarning("Embedded passphrase is empty. Cannot decrypt students.enc.");
                    return;
                }

                byte[] encryptedBytes = File.ReadAllBytes(targetPath);
                string csvText = DecryptStudentsCsv(encryptedBytes, passphrase);
                var lines = csvText
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToArray();

                if (lines.Length <= 1) return;

                string header = lines[0].Trim();
                if (!string.Equals(header, "student_number,name,code", StringComparison.Ordinal))
                {
                    _logger.LogWarning("Invalid students header: {Header}", header);
                    return;
                }

                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length != 3) continue;

                    if (ushort.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort num))
                    {
                        _studentMap[num] = new StudentInfo
                        {
                            StudentNumber = num,
                            Name = parts[1].Trim(),
                            Code = parts[2].Trim(),
                            Email = FormatStudentEmail(num)
                        };
                    }
                }

                _logger.LogInformation("Successfully loaded {Count} students from {Path}", _studentMap.Count, targetPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load/decrypt students.enc");
            }
        }

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

            using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
            byte[] key = kdf.GetBytes(KeySize);
            byte[] plaintextBytes = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            return Encoding.UTF8.GetString(plaintextBytes).TrimStart('\uFEFF');
        }

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
    }
}
