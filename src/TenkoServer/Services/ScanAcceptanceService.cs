using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace TenkoServer.Services
{
    public interface IScanAcceptanceService
    {
        /// <summary>クライアントからの点呼データ受付を受け入れるかどうか (本番停止にも使用)</summary>
        bool IsAcceptingScans { get; set; }
    }

    /// <summary>
    /// 点呼データの受付可否を保持するサービス。
    /// 状態は data/scan_acceptance.json に永続化され、再起動後も維持される。
    /// (本番停止の用途で、意図せず受付が再開される事故を防ぐため)
    /// 既定値は true (受け入れる)。ファイルが存在しない/破損している場合は true で起動する。
    /// </summary>
    public class ScanAcceptanceService : IScanAcceptanceService
    {
        private const string StateFileName = "scan_acceptance.json";

        private readonly ILogger<ScanAcceptanceService> _logger;
        private readonly string _stateFilePath;
        private readonly object _lock = new();
        private bool _isAcceptingScans = true;

        private sealed class State
        {
            [JsonPropertyName("isAcceptingScans")]
            public bool IsAcceptingScans { get; set; } = true;
        }

        public ScanAcceptanceService(IWebHostEnvironment env, ILogger<ScanAcceptanceService>? logger = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ScanAcceptanceService>.Instance;

            string dataDir = Path.Combine(env.ContentRootPath, "data");
            _stateFilePath = Path.Combine(dataDir, StateFileName);

            Load();
        }

        public bool IsAcceptingScans
        {
            get { lock (_lock) return _isAcceptingScans; }
            set
            {
                lock (_lock)
                {
                    if (_isAcceptingScans == value) return;
                    _isAcceptingScans = value;
                }
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                {
                    return;
                }

                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFilePath));
                if (state != null)
                {
                    _isAcceptingScans = state.IsAcceptingScans;
                    _logger.LogInformation("Scan acceptance state restored: {IsAcceptingScans} (from {Path})",
                        _isAcceptingScans, _stateFilePath);
                }
            }
            catch (Exception ex)
            {
                // 破損時は既定値 (受付) で起動し、運用を妨げない
                _logger.LogWarning(ex, "Failed to load scan acceptance state. Using default (accepting).");
            }
        }

        private void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 一時ファイル経由の原子的書き込み
                string tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(new State { IsAcceptingScans = _isAcceptingScans }));
                File.Move(tempPath, _stateFilePath, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist scan acceptance state to {Path}", _stateFilePath);
            }
        }
    }
}
