namespace AsetLauncher.Models
{
    public sealed class LauncherAccount
    {
        public string Id { get; set; } = "";

        public string Type { get; set; } = "offline";

        public string Nickname { get; set; } = "";

        public string Uuid { get; set; } = "";

        public string AccessToken { get; set; } = "";

        public string ClientToken { get; set; } = "";

        public string UserType { get; set; } = "legacy";

        public string AvatarPath { get; set; } = "";

        public string CreatedAtUtc { get; set; } = "";
    }
}
