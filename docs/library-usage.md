# Patchy Library Usage Guide

The `Patchy` library allows your .NET application to download and apply updates securely.

## Installation

Install via NuGet:

```bash
dotnet add package Patchy
```

## Basic Usage

### 1. Initialize the Updater

Create an instance of `PatchyUpdater`. You need:
- The URL where your update manifest (`meta.json` or `info.json`) is hosted.
- Your **Public Key** (PEM format).

```csharp
using Patchy;

const string PublicKey = @"-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...
-----END PUBLIC KEY-----";

const string ManifestUrl = "https://example.com/updates/meta.json";

// The callback is used to ask the user if they want to download a FULL package
// in case the delta update fails or is not possible.
var updater = new PatchyUpdater(ManifestUrl, PublicKey, async () => 
{
    // Return true to allow full download, false to cancel
    var result = await MessageBox.Show("Full download required. Proceed?");
    return result == DialogResult.Yes;
});
```

### 2. Check for Updates

Only retrieves the manifest and verifies the signature. Does not download files yet.

```csharp
try 
{
    // For file-level updates (recommended):
    // Note: You might need to fetch the JSON manually first if you want to inspect VersionId
    // before calling ApplyUpdatePackageAsync, as ApplyUpdatePackageAsync
    // takes a local path to the package.
    
    // Common pattern: Use a separate lightweight check or download meta.json first.
    using var client = new HttpClient();
    var json = await client.GetStringAsync(ManifestUrl);
    var remoteManifest = JsonConvert.DeserializeObject<UpdatePackageManifest>(json);
    
    if (remoteManifest.VersionId > currentVersionId)
    {
        Console.WriteLine("Update available!");
    }
}
catch (CryptographicException ex)
{
    Console.WriteLine("Security warning: Manifest signature invalid!");
}
```

### 3. Download and Apply Update Package

If an update is available, download the `update.pkg` and apply it.

```csharp
// 1. Download the package
var packageUrl = "https://example.com/updates/update.pkg";
var localPackagePath = Path.Combine(Path.GetTempPath(), "update.pkg");

using (var client = new HttpClient())
{
    var bytes = await client.GetByteArrayAsync(packageUrl);
    await File.WriteAllBytesAsync(localPackagePath, bytes);
}

// 2. Apply the package
try
{
    string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
    
    // This methods verifies the package signature AND all file hashes
    var manifest = await updater.ApplyUpdatePackageAsync(localPackagePath, appDirectory);
    
    Console.WriteLine($"Successfully updated to version {manifest.Version}");
    
    // Restart application
    RestartApp(); 
}
catch (CryptographicException ex)
{
    Console.WriteLine($"Update failed security check: {ex.Message}");
    // Do NOT trust the applied files - rollback might be needed (not built-in)
}
```

## Advanced: Handling Rollbacks

`ApplyUpdatePackageAsync` modifies files in place. For a production environment, consider:

1. Copying your app to a `staging` directory.
2. Applying the update to `staging`.
3. Swapping directories or verifying `staging` launches correctly.

## Error Handling

- **`CryptographicException`**: Thrown if signature verification fails or file hashes don't match. **Do not ignore this.** It means potential tampering.
- **`InvalidDataException`**: Malformed JSON or package structure.
- **`FileNotFoundException`**: Missing files in the package.
