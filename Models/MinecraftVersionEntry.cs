using System;

namespace AsetLauncher.Models
{
    public sealed class MinecraftVersionEntry
    {
        public string Id { get; set; }

        public string Type { get; set; }

        public DateTime ReleaseTime { get; set; }

        public string MetadataUrl { get; set; }

        public string DisplayName { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
        }
    }
}
