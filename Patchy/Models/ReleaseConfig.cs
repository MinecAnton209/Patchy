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
    }
}