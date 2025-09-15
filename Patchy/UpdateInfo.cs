using System.Collections.Generic;

namespace Patchy
{
    public class UpdateInfo
    {
        public required long VersionId { get; set; }
        public required string Version { get; set; }
        public required string ReleaseName { get; set; }
        public required string ReleaseType { get; set; }
        public required List<string> Changes { get; set; }
        public required string DownloadUrl { get; set; }
        public required string FileHash { get; set; }
        public required string Signature { get; set; }
    }
}