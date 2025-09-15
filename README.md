# Patchy

[![NuGet Version](https://img.shields.io/nuget/v/Patchy.svg)](https://www.nuget.org/packages/Patchy/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A simple, secure, ECDsa-based self-updater library for .NET applications.

Patchy provides a secure way to deliver updates to your users. It ensures that update information is authentic (signed by the developer) and that the update files are integral (not corrupted or tampered with).

## Features

- **Modern Security**: Uses ECDsa (Elliptic Curve Digital Signature Algorithm) for verifying update manifest authenticity.
- **Integrity Check**: Verifies update packages with SHA256 hashes.
- **Developer-Friendly Tooling**: Comes with a command-line tool (`Patchy.Tool`) to automate the release process (key generation, hashing, and signing).
- **Easy Integration**: A single class `PatchyUpdater` to drop into your application.
- **Open Source**: Licensed under MIT.

## Getting Started

The project is divided into two parts:
1.  **`Patchy`**: The NuGet library you include in your application.
2.  **`Patchy.Tool`**: The console utility you use to prepare and sign your releases.

### 1. Using `Patchy` in Your Application

First, install the package from NuGet:
```bash
dotnet add package Patchy
```

Then, use the `PatchyUpdater` class to check for updates. You will need the public key you generated with `Patchy.Tool`.

```csharp
// The public key you generated with Patchy.Tool.
// It's safe to embed this directly in your code.
const string MyPublicKey = @"-----BEGIN PUBLIC KEY-----
...Your Public Key Content...
-----END PUBLIC KEY-----";

// The URL to your remote info.json file.
const string InfoJsonUrl = "https://your.server.com/path/to/info.json";

public async Task CheckForUpdates()
{
    try 
    {
        var updater = new PatchyUpdater(InfoJsonUrl, MyPublicKey);
        // This will throw a CryptographicException if the signature is invalid.
        UpdateInfo updateInfo = await updater.CheckForUpdatesAsync();

        if (updateInfo.VersionId > CurrentAppVersionId)
        {
            Console.WriteLine($"New version available: {updateInfo.Version}");
            // Ask the user if they want to update. If yes:
            
            // This will throw a CryptographicException if the hash is invalid.
            string downloadedFile = await updater.DownloadUpdateAsync(updateInfo);
            
            Console.WriteLine($"Update downloaded to: {downloadedFile}");
            // Now you can launch an external process to apply the update.
        }
    }
    catch (CryptographicException ex)
    {
        // Handle security errors (tampered files)
        Console.WriteLine($"A security error occurred: {ex.Message}");
    }
    catch (Exception ex)
    {
        // Handle other errors (network issues, etc.)
        Console.WriteLine($"An error occurred: {ex.Message}");
    }
}
```

### 2. Preparing a Release with `Patchy.Tool`

The `Patchy.Tool` utility makes preparing a release a simple, one-command process.

#### One-Time Setup: Generate Keys

Run this command once to create your cryptographic keys.
```bash
Patchy.Tool.exe generate-keys
```
This will create `privateKey.pem` (keep this **SECRET**) and `publicKey.pem` (embed this in your application).

#### For Every Release: Sign the Update

1.  Prepare your update package (e.g., `Update.zip`).
2.  Update your `info.json` with the new version details, changelog, etc. You can leave `FileHash` and `Signature` empty.
3.  Run the `sign` command:

```bash
Patchy.Tool.exe sign path/to/info.json path/to/privateKey.pem path/to/Update.zip
```
The tool will automatically calculate the hash of `Update.zip`, place it inside `info.json`, and then sign the entire `info.json` file with your private key.

Now you are ready to upload `Update.zip` and the signed `info.json` to your server.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.