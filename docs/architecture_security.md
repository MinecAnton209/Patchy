# Architecture & Security

## Security Model

Patchy is designed with a "trust-on-first-use" and "asymmetric cryptography" model.

### 1. ECDsa Signatures
- **Curve**: NIST P-256
- **Hashing**: SHA256

All update manifests (`meta.json`) are signed with the developer's **Private Key**.
The application contains the embedded **Public Key**.

When an update is checked:
1. Manifest is downloaded.
2. The `Signature` field is detached.
3. The JSON content is canonicalized.
4. ECDsa verification is performed against the Public Key.

**Result:** An attacker cannot fake a version number or file hash without the Private Key.

### 2. Hash Verification
Every file action in the update package includes a SHA256 hash.

- **Source Hash**: Before applying a binary patch to a file, the *current local file's* hash is checked. If it doesn't match the expected source hash, the patch is aborted (prevents corruption if the user modified local files).
- **Target Hash**: After applying a patch or downloading a new file, the *resulting file's* hash is checked.

### 3. Fallback Components Security
If a fallback installer or full package is used:
- Their hashes (`FallbackInstallerHash`, `FullPackageHash`) are included in the signed manifest.
- The updater MUST verify these hashes after extraction and before execution.
- This ensures the fallback installer hasn't been swapped by an attacker.

## Delta Update Mechanism (bsdiff)

Patchy uses **bsdiff** (binary suffix diff) for efficient delta updates.

### How it works
1. **Creation**: `Patchy.Tool` compares `old_file.dll` and `new_file.dll`. It generates a compact patch file containing only the differences and instructions to reconstruct the new file.
2. **Application**: The library reads `old_file.dll`, applies the patch, and writes `new_file.dll`.

### Benefits
- **Small Size**: Changing one line of code in a 10MB DLL results in a tiny patch (KB), not a 10MB download.
- **Speed**: Faster downloads for users.

## Package Format

`update.pkg` is a standard ZIP file.

```
/
├── meta.json        (Manifest)
├── diffs/           (Patch files folder)
└── add/             (New files folder)
```

This simple structure allows for easy inspection and debugging.
