using System.Collections.Generic;

namespace AsetLauncher.Models
{
    public sealed class LauncherSettings
    {
        public int MaxRamMb { get; set; } = 2048;

        public bool ShowSnapshots { get; set; } = false;
        public bool ShowFabricVersions { get; set; } = true;
        public bool ShowForgeVersions { get; set; } = true;

        public string ThemeId { get; set; } = "default";

        public bool MusicEnabled { get; set; } = true;

        public int MusicVolume { get; set; } = 35;

        public string MusicTrackId { get; set; } = "";

        public string BackendBaseUrl { get; set; } = "https://asetlauncher.ru";

        public List<LauncherAccount> Accounts { get; set; } = new List<LauncherAccount>();

        public string SelectedAccountId { get; set; } = "";

        // Legacy single-profile fields kept for migration compatibility.
        public string PlayerNickname { get; set; } = "";

        public string PlayerAvatarPath { get; set; } = "";
    }
}
