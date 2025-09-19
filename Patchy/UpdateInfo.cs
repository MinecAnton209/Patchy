using System.Collections.Generic;
using Newtonsoft.Json;

namespace Patchy
{
    public class UpdateInfo
    {
        public long VersionId { get; set; }
        public string Version { get; set; } = string.Empty;
        public string ReleaseName { get; set; } = string.Empty;
        public string ReleaseType { get; set; } = string.Empty;
        public List<string> Changes { get; set; } = new();
        public List<PatchFileEntry> Files { get; set; } = new(); 
        public string DownloadUrl { get; set; } = string.Empty;
        public string FileHash { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    /// <summary>
    /// Describes a release that consists of multiple patch/new/delete operations.
    /// This is the main manifest file for binary patch updates.
    /// </summary>
    public class PatchManifest
    {
        public long VersionId { get; set; }
        public string Version { get; set; } = string.Empty;
        public long FromVersionId { get; set; }
        public string ReleaseName { get; set; } = string.Empty;
        public List<string> Changes { get; set; } = new();
        public List<PatchFileEntry> Files { get; set; } = new();
        public string PatchUrlBase { get; set; } = string.Empty;
        public string? Signature { get; set; }
    }

    /// <summary>
    /// Describes a single file operation within a PatchManifest.
    /// </summary>
    public class PatchFileEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Hash { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SourceHash { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? TargetHash { get; set; }
    }
    
    public class SinglePatchManifest
    {
        public long VersionId { get; set; }
        public string Version { get; set; } = string.Empty;
        public long FromVersionId { get; set; }
        public string ReleaseName { get; set; } = string.Empty;
        public List<string> Changes { get; set; } = new();
        public string PatchFile { get; set; } = string.Empty;
        public string PatchHash { get; set; } = string.Empty;
        public string SourceArchiveHash { get; set; } = string.Empty;
        public string TargetArchiveHash { get; set; } = string.Empty;
        public string PatchUrlBase { get; set; } = string.Empty;
        public string? Signature { get; set; }
    }
}