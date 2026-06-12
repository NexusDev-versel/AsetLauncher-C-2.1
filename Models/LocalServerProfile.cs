using System.Collections.Generic;

namespace AsetLauncher.Models
{
    public sealed class LocalServerProfile
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Core { get; set; } = "Purpur";

        public string Version { get; set; } = "1.20.1";

        public int RamMb { get; set; } = 2048;

        public string ExtraJavaArgs { get; set; } = string.Empty;

        public string FolderPath { get; set; } = string.Empty;

        public string JarFileName { get; set; } = string.Empty;

        public string InstalledCoreVersion { get; set; } = string.Empty;

        public string CreatedAtUtc { get; set; } = string.Empty;

        public string DisplayName
        {
            get
            {
                return (Name ?? string.Empty) + " [" + (Core ?? string.Empty) + " " + (Version ?? string.Empty) + "]";
            }
        }
    }

    public sealed class LocalServerStorage
    {
        public List<LocalServerProfile> Servers { get; set; } = new List<LocalServerProfile>();
    }
}
