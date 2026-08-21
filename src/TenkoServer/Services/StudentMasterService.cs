using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenkoServer.Models;
using TenkoServer.Models.DTOs;

namespace TenkoServer.Services
{
    /// <summary>
    /// 学籍番号のみを保持する学生マスタ。
    /// 氏名等の個人情報はサーバーに置かず、data/students.txt (1行1学籍番号) を読み込む。
    /// </summary>
    public interface IStudentMasterService
    {
        /// <summary>マスタ登録人数</summary>
        int TotalCount { get; }

        /// <summary>マスタに登録された全学籍番号</summary>
        IReadOnlyCollection<ushort> GetAllStudentNumbers();

        /// <summary>マスタに登録されていない学籍番号 (未点呼者) を返す</summary>
        List<UnverifiedStudentDto> GetUnverifiedStudents(IEnumerable<ushort> scannedStudentNumbers);
    }

    public class StudentMasterService : IStudentMasterService
    {
        private const string StudentListFileName = "students.txt";

        private readonly TenkoServerOptions _options;
        private readonly ILogger<StudentMasterService> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly HashSet<ushort> _studentNumbers = new();

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

        public int TotalCount => _studentNumbers.Count;

        public IReadOnlyCollection<ushort> GetAllStudentNumbers() => _studentNumbers;

        public List<UnverifiedStudentDto> GetUnverifiedStudents(IEnumerable<ushort> scannedStudentNumbers)
        {
            var scannedSet = new HashSet<ushort>(scannedStudentNumbers);
            return _studentNumbers
                .Where(n => !scannedSet.Contains(n))
                .OrderBy(n => n)
                .Select(n => new UnverifiedStudentDto { StudentNumber = n })
                .ToList();
        }

        private void LoadStudents()
        {
            // 探索ディレクトリ: 実行ディレクトリ, コンテンツルート, コンテンツルートの2階層上
            var candidateDirs = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                _env.ContentRootPath,
                Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", ".."))
            };

            string? targetPath = candidateDirs
                .Select(d => Path.Combine(d, "data", StudentListFileName))
                .FirstOrDefault(File.Exists);

            if (targetPath == null)
            {
                _logger.LogWarning("students.txt not found in search paths. Master student list will be empty.");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(targetPath);
                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();

                    // 空行と # コメント行は無視
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    if (ushort.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out ushort num))
                    {
                        _studentNumbers.Add(num);
                    }
                    else
                    {
                        _logger.LogWarning("Skipped invalid student number line: {Line}", line);
                    }
                }

                _logger.LogInformation("Successfully loaded {Count} student numbers from {Path}", _studentNumbers.Count, targetPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load students.txt");
            }
        }
    }
}
