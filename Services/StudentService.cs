using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tenko.Native.Services
{
    public class StudentInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class StudentService
    {
        private readonly Dictionary<ushort, StudentInfo> _studentMap = new();

        public StudentService()
        {
            LoadStudents();
        }

        private void LoadStudents()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string csvPath = Path.Combine(baseDir, "data", "students.csv");

            if (!File.Exists(csvPath)) return;

            try
            {
                var lines = File.ReadAllLines(csvPath);
                if (lines.Length <= 1) return;

                // Skip header: student_number,id,name,code
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        if (ushort.TryParse(parts[0], out ushort studentNumber))
                        {
                            _studentMap[studentNumber] = new StudentInfo
                            {
                                Name = parts[2].Trim(),
                                Code = parts[3].Trim()
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StudentService] Load failed: {ex.Message}");
            }
        }

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
