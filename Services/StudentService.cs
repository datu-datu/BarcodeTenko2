using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tenko.Native.Services
{
    public class StudentInfo
    {
        public ushort StudentNumber { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class StudentService
    {
        private readonly Dictionary<ushort, StudentInfo> _students = new();

        public StudentService()
        {
            LoadStudents();
        }

        private void LoadStudents()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "students.csv");
            if (!File.Exists(path)) return;

            try
            {
                // student_number, id, name, code
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (var line in lines.Skip(1)) // Skip header
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        if (ushort.TryParse(parts[0], out ushort studentNum))
                        {
                            _students[studentNum] = new StudentInfo
                            {
                                StudentNumber = studentNum,
                                Id = parts[1].Trim(),
                                Name = parts[2].Trim(),
                                Code = parts[3].Trim()
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StudentService] LoadStudents failed: {ex.Message}");
            }
        }

        public StudentInfo? GetStudent(ushort studentNumber)
        {
            return _students.TryGetValue(studentNumber, out var student) ? student : null;
        }
    }
}
