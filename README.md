The library supports two update strategies:
- **Full package updates**: Replace the entire application with a new version
- **File-level delta updates**: Patch only modified files using bsdiff binary diffing

## Features

- ECDsa digital signatures for manifest verification
- SHA256 hash verification for all files
- Per-file binary patching with bsdiff
- Automatic detection of added, modified, and removed files
- Command-line tool for release preparation
- Cross-platform .NET 8.0 support


## Documentation

- [**CLI Usage Guide**](docs/cli-usage.md): How to use `Patchy.Tool` to create and sign updates.
- [**Library Usage Guide**](docs/library-usage.md): How to integrate `Patchy` into your application code.
- [**Architecture & Security**](docs/architecture_security.md): Understanding the security model and binary patching.

## Components

| Component | Description |
|-----------|-------------|
| `Patchy` | NuGet library for your application |
| `Patchy.Tool` | CLI utility for creating and signing releases |

## Installation

```bash
dotnet add package Patchy
```

## Quick Start

### 1. Generate Keys (One-Time Setup)

```bash
Patchy.Tool.exe generate-keys
```

This creates:
- `privateKey.pem` - Keep this secret, use for signing
- `publicKey.pem` - Embed in your application for verification

### 2. Create an Update Package

Compare two versions of your application and generate a delta update:

```bash
Patchy.Tool.exe create-update-package <old_dir> <new_dir> <output_dir> <private_key> [config.json]
```

Example:
```bash
Patchy.Tool.exe create-update-package v1.0 v1.1 release privateKey.pem config.json
```

### 3. Apply Updates in Your Application

```csharp
const string PublicKey = @"-----BEGIN PUBLIC KEY-----
...your public key...
-----END PUBLIC KEY-----";

var updater = new PatchyUpdater(manifestUrl, PublicKey, () => Task.FromResult(true));

// Download and apply file-level update package
var manifest = await updater.ApplyUpdatePackageAsync("update.pkg", targetDirectory);
Console.WriteLine($"Updated to version {manifest.Version}");
```

## Update Package Structure

The `create-update-package` command generates a signed ZIP archive:

```
update.pkg
├── meta.json           # Signed manifest with file actions
├── diffs/              # Binary patches for modified files
│   ├── file1.dll.patch
│   └── file2.exe.patch
└── add/                # New files (full content)
    └── newfile.dll
```

### Manifest Format (meta.json)

```json
{
  "VersionId": 1733421600,
  "Version": "1.1.0",
  "FromVersionId": 1733335200,
  "ReleaseName": "Update 1.1.0",
  "Changes": ["Bug fixes", "New feature"],
  "Files": [
    {
      "Path": "lib/core.dll",
      "Action": "modified",
      "PatchFile": "diffs/lib_core.dll.patch",
      "SourceHash": "abc123...",
      "TargetHash": "def456..."
    },
    {
      "Path": "plugins/new.dll",
      "Action": "added",
      "AddFile": "add/plugins_new.dll",
      "TargetHash": "789abc..."
    },
    {
      "Path": "old/legacy.dll",
      "Action": "removed"
    }
  ],
  "Signature": "base64signature..."
}
```

### File Actions

| Action | Description |
|--------|-------------|
| `modified` | File exists in both versions with different content. A bsdiff patch is created. |
| `added` | File only exists in the new version. Full file is included. |
| `removed` | File only exists in the old version. Will be deleted on update. |

## Configuration File

Optional configuration for `create-update-package`:

```json
{
  "NewVersionId": 1733421600,
  "Version": "1.1.0",
  "FromVersionId": 1733335200,
  "ReleaseName": "Update 1.1.0",
  "Changes": [
    "Fixed critical bug",
    "Added new feature"
  ]
}
```

## API Reference

### PatchyUpdater

```csharp
public class PatchyUpdater
{
    // Constructor
    public PatchyUpdater(string infoUrl, string publicKeyPem, Func<Task<bool>> confirmFullDownloadCallback);

    // Apply a file-level update package
    public Task<UpdatePackageManifest> ApplyUpdatePackageAsync(string packagePath, string targetDirectory);

    // Apply a single bsdiff patch
    public Task ApplyPatchAsync(string oldFilePath, string patchFilePath, string newFilePath);
}
```

### UpdatePackageManifest

```csharp
public class UpdatePackageManifest
{
    public long VersionId { get; set; }
    public string Version { get; set; }
    public long FromVersionId { get; set; }
    public string ReleaseName { get; set; }
    public List<string> Changes { get; set; }
    public List<FileAction> Files { get; set; }
    public string Signature { get; set; }
}

public class FileAction
{
    public string Path { get; set; }        // Relative file path
    public string Action { get; set; }      // "modified", "added", "removed"
    public string PatchFile { get; set; }   // Path to patch file (modified)
    public string AddFile { get; set; }     // Path to new file (added)
    public string SourceHash { get; set; }  // SHA256 of original file
    public string TargetHash { get; set; }  // SHA256 of target file
}
```

## CLI Commands

### generate-keys
Generate ECDsa key pair for signing.

```bash
Patchy.Tool.exe generate-keys
```

### create-update-package
Create a file-level update package with per-file patches.

```bash
Patchy.Tool.exe create-update-package <old_dir> <new_dir> <output_dir> <private_key> [config.json]
```

### create-patch
Create a single bsdiff patch between two files.

```bash
Patchy.Tool.exe create-patch <old_file> <new_file> <patch_output>
```

### apply-patch
Apply a bsdiff patch to recreate the new file.

```bash
Patchy.Tool.exe apply-patch <old_file> <patch_file> <new_file_output>
```

### hash
Calculate SHA256 hash of a file.

```bash
Patchy.Tool.exe hash <file_path>
```

## Security

- All manifests are signed with ECDsa (NIST P-256 curve)
- File integrity is verified with SHA256 hashes
- Source file hash is verified before applying patches
- Target file hash is verified after applying patches
- Signature verification fails if manifest is tampered

## Dependencies

- [BsDiff](https://github.com/LogosBible/bsdiff.net) - Binary diff/patch library
- [Newtonsoft.Json](https://www.newtonsoft.com/json) - JSON serialization
- [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) - ZIP/TAR archive support

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.