namespace Patchy
{
    public class UpdateInfo
    {
        public long VersionId { get; set; }
        public string Version { get; set; }
        public string ReleaseName { get; set; }
        public string ReleaseType { get; set; }
        public List<string> Changes { get; set; }
        public string DownloadUrl { get; set; }
        public string FileHash { get; set; }
        public string Signature { get; set; }
    }
}