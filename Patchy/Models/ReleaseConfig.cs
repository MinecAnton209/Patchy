using Newtonsoft.Json;

namespace Patchy.Models
{
    public class ReleaseConfig
    {
        [JsonConstructor]
        public ReleaseConfig() { }

        public long NewVersionId { get; set; }
        public string Version { get; set; } = string.Empty;
        public long FromVersionId { get; set; }
        public string ReleaseName { get; set; } = string.Empty;
        public List<string> Changes { get; set; } = new();
        public string PatchUrlBase { get; set; } = string.Empty;
        public string? FullPackageFile { get; set; }
        
        public bool RestartRequired { get; set; } = true;
        public bool Critical { get; set; } = false;

        public string InstallerFile { get; set; } = string.Empty;
        public string InstallerArguments { get; set; } = string.Empty;
        public string InstallerFileHash { get; set; } = string.Empty;
    }
}