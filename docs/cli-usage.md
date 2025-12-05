# Patchy.Tool CLI Guide

`Patchy.Tool` is a command-line utility for managing update packages and cryptographic keys.

## Installation

The tool is built as a self-contained executable.

## Commands

### `generate-keys`

Generates an ECDsa key pair (NIST P-256 curve).

```bash
Patchy.Tool.exe generate-keys
```

**Output:**
- `privateKey.pem`: Use this to sign updates. **Keep secret.**
- `publicKey.pem`: Embed this in your application. Safe to distribute.

---

### `create-update-package`

Creates a file-level update package by comparing two directory versions.

```bash
Patchy.Tool.exe create-update-package <old_dir> <new_dir> <output_dir> <private_key> [config.json]
```

**Arguments:**
- `old_dir`: Path to the directory containing the *previous* version of your app.
- `new_dir`: Path to the directory containing the *new* version of your app.
- `output_dir`: Where to save the `update.pkg`.
- `private_key`: Path to your `privateKey.pem`.
- `config.json`: (Optional) Metadata configuration (version name, changelog).

**Output:**
- `update.pkg`: ZIP archive containing the manifest and patch files.
- `meta.json`: The signed manifest (extracted for convenience).

**Example:**
```bash
Patchy.Tool.exe create-update-package ./v1.0 ./v1.1 ./release privateKey.pem config.json
```

---

### `prepare-release` (Legacy/Full Update)

Prepares a release that patches a *full directory* tarball or creates a full installer package.

```bash
Patchy.Tool.exe prepare-release <old_dir> <new_dir> <output_dir> <private_key> <config.json> <installer_exe>
```

**Use this if:** You want to distribute a single binary patch file that patches an archive of the entire application, or a full installer.

---

### `sign`

Manually signs an existing update package and manifest.

```bash
Patchy.Tool.exe sign <info_json> <private_key> <update_package>
```

**Arguments:**
- `info_json`: Path to the `info.json` manifest.
- `private_key`: Path to `privateKey.pem`.
- `update_package`: Path to the file (ZIP/EXE) to hash.

---

### `hash`

Calculates the SHA256 hash of a file (useful for verifying integrity manually).

```bash
Patchy.Tool.exe hash <file_path>
```

---

### `create-patch` & `apply-patch`

Low-level commands to create/apply a single bsdiff patch.

```bash
Patchy.Tool.exe create-patch <old_file> <new_file> <patch_file>
Patchy.Tool.exe apply-patch <old_file> <patch_file> <output_file>
```
