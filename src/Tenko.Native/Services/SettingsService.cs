using System.Collections.Generic;

namespace Tenko.Native.Services
{
    public class AppSettings
    {
        public string Location { get; set; } = string.Empty;
    }

    public class SettingsService
    {
        private readonly StorageService _storage;
        private readonly string _settingsPath;
        private readonly string _locationsPath;
        private AppSettings _settings = new();
        private List<string> _locations = new() { "2棟2階", "第一体育館前", "本部横" };

        public SettingsService(StorageService storage)
        {
            _storage = storage;
            _settingsPath = _storage.GetDataPath("settings.json");
            _locationsPath = _storage.GetDataPath("locations.json");
            
            _settings = _storage.LoadJson<AppSettings>(_settingsPath) ?? new();
            LoadLocations();
        }

        public string Location
        {
            get => _settings.Location;
            set { _settings.Location = value; Save(); }
        }

        public List<string> Locations => _locations;

        public void Save() => _storage.SaveJson(_settingsPath, _settings);

        private void LoadLocations()
        {
            var loaded = _storage.LoadJson<List<string>>(_locationsPath);
            if (loaded != null) _locations = loaded;
            else _storage.SaveJson(_locationsPath, _locations, indent: true);
        }
    }
}
