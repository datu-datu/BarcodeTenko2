using System.Collections.Generic;
using System.Linq;
using Tenko.Lite.Generated;

namespace Tenko.Lite.Services
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
        private List<string> _locations = new();

        public SettingsService(StorageService storage)
        {
            _storage = storage;
            _settingsPath = _storage.GetDataPath("settings.json");
            _locationsPath = _storage.GetDataPath("locations.json");
            
            _locations = EmbeddedLocations.GetLocations().ToList();
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
            if (loaded != null && loaded.Count > 0)
            {
                _locations = loaded;
            }
            else
            {
                _locations = EmbeddedLocations.GetLocations().ToList();
                _storage.SaveJson(_locationsPath, _locations, indent: true);
            }
        }
    }
}
