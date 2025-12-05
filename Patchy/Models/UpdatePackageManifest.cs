using Newtonsoft.Json;

namespace Patchy.Models
{
    /// <summary>
    /// Manifest for the update package (meta.json).
    /// Describes all file operations needed to update from one version to another.
    /// </summary>
    public class UpdatePackageManifest
    {
        public long VersionId { get; set; }
        public string Version { get; set; } = string.Empty;
        public long FromVersionId { get; set; }
        public string ReleaseName { get; set; } = string.Empty;
        public List<string> Changes { get; set; } = new();
        public List<FileAction> Files { get; set; } = new();

        public bool RestartRequired { get; set; } = true;
        public bool Critical { get; set; } = false;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FallbackInstallerFile { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FallbackInstallerHash { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FallbackInstallerArguments { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FullPackageFile { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FullPackageHash { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? Signature { get; set; }
    }

    /// <summary>
    /// Describes a single file operation within an update package.
    /// </summary>
    public class FileAction
    {
        /// <summary>
        /// Relative path to the file (e.g., "bin/app.dll")
        /// </summary>
        public string Path { get; set; } = string.Empty;
        
        /// <summary>
        /// Action type: "modified", "added", or "removed"
        /// </summary>
        public string Action { get; set; } = string.Empty;
        
        /// <summary>
        /// For "modified": relative path to the patch file (e.g., "diffs/bin_app.dll.patch")
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? PatchFile { get; set; }
        
        /// <summary>
        /// For "added": relative path to the new file (e.g., "add/new_icon.png")
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? AddFile { get; set; }
        
        /// <summary>
        /// Hash of the file inside the package (the .patch file or the new added file).
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? PackageFileHash { get; set; }
        
        /// <summary>
        /// SHA256 hash of the original file (for "modified" actions)
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SourceHash { get; set; }
        
        /// <summary>
        /// SHA256 hash of the target file (for "modified" and "added" actions)
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? TargetHash { get; set; }
    }
}
