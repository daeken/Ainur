using System.Security.Cryptography;
using System.Text.Json;

namespace Ainur.Supervisor;

/// <summary>
/// Pre-spawn content gate. This list is the independently staged single full-UI payload,
/// not a general release builder or a claim that a second rollback image is attested.
/// Reject all unknown IDs, changed/missing/extra files, links and malformed manifest data.
/// Recheck at the last boundary before EVERY Process.Start (initial, upgrade, recovery,
/// rollback); the single-use spawn lease remains independently required.
/// </summary>
public static class StrictReleaseGate {
	public const string ReleaseId = "r20261003-ulmo-fullui-18005e8-4d6baf032671";
	public const string ReleaseJsonSha256 = "55A91E8FE46D69BE72AA0CDAD8A72F161BB1DE8211CFB43B52B9907584368385";
	public const string ManifestSha256 = "482AC381952C9897CC4B6C1DE30863C323D28DD25C1E3C28A49C4249577AE189";
	public const string CoreSha256 = "7A440EE4FC87C68ADD1C5BC472AB2C2E7E18260E8FAFC88C89D24F37B554965F";
	public const string ServerSha256 = "214D53A1CE0F79639498E8F70C8059DB8D6B4AFDC1ADF0729EBD473659DC865B";
	public const string ExecutableSha256 = "1E8391DC3F39C17E89FC3E2D41745A2AF7237A14E3EDCB5C77A4B610C5B72CB9";
	public const string SourceCommit = "18005e8c61f61c11c4c931d3d5cd87438c231a24";

	sealed record PinnedFile(string relative_path, long size_bytes, string sha256);

	public static bool IsAcceptedTarget(string? releaseId) =>
		string.Equals(releaseId, ReleaseId, StringComparison.Ordinal);

	public static bool IsIndependentRollback(string? current, string? previous) =>
		// NO SECOND approved distinct payload or strict receipt exists. Fail closed.
		false;

	// Content equality is not sufficient when the supervisor's UID can replace a
	// DLL between hash verification and dotnet opening it (or later dependency
	// loads). These checks are deliberately independent of Verify: the staged
	// UID-501 /tmp artifact may pass content verification, but MUST NOT spawn.
	// A positive protected-install proof requires every path component and file
	// to be OS-owned by a different, privileged UID, with no group/world write,
	// hardlinks, ACL write delegation or unsafe xattrs. No install is done here.
	internal static bool SafeProtectionRecord(uint ownerUid, uint supervisorUid, UnixFileMode mode,
		bool isDirectory, uint hardLinks, bool aclPresent) {
		const UnixFileMode writable = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
		if(supervisorUid == 0 || ownerUid != 0 || aclPresent || (mode & writable) != 0) return false;
		return isDirectory ? hardLinks >= 1 : hardLinks == 1;
	}

	// Darwin-specific protection proof. Never claim a protected installation on
	// another OS or when stat/ACL inspection is unavailable or ambiguous.
	// stat reports owner, raw mode, hard-link count; ls -ldeO@ reports macOS ACL
	// and file flags (+ marks ACL). Unknown output is fail-closed.
	static bool TryStat(string path, out uint uid, out UnixFileMode mode, out uint links, out bool aclPresent) {
		uid = links = 0; aclPresent = true; mode = default;
		if(!OperatingSystem.IsMacOS()) return false;
		try {
			using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat") {
				UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
			} };
			process.StartInfo.ArgumentList.Add("-f");
			process.StartInfo.ArgumentList.Add("%u|%p|%l");
			process.StartInfo.ArgumentList.Add(path);
			if(!process.Start()) return false;
			var line = process.StandardOutput.ReadToEnd().Trim();
			if(!process.WaitForExit(2000) || process.ExitCode != 0) return false;
			var parts = line.Split('|');
			if(parts.Length != 3 || !uint.TryParse(parts[0], out uid) ||
				!uint.TryParse(parts[2], out links)) return false;
			var rawMode = Convert.ToUInt32(parts[1], 8);
			mode = (UnixFileMode) (rawMode & 0xFFF);
			using var ls = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/ls") {
				UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
			} };
			ls.StartInfo.ArgumentList.Add("-ldeO@");
			ls.StartInfo.ArgumentList.Add(path);
			if(!ls.Start()) return false;
			var detail = ls.StandardOutput.ReadToEnd().TrimEnd();
			if(!ls.WaitForExit(2000) || ls.ExitCode != 0) return false;
			var lines = detail.Split('\n');
			var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if(first.Length < 7 || first[0].Length is not (10 or 11) ||
				first[0][0] is not ('-' or 'd') || !uint.TryParse(first[1], out var lsLinks) ||
				lsLinks != links) return false;
			// Apple SIP's restricted/hidden flags on system-owned /usr do not
			// delegate write access; reject all other unknown flags. SIP's xattr
			// `com.apple.rootless` has no permission delegation and is the ONLY
			// accepted extended attribute (ordinary apps cannot remove SIP).
			if(first[4] != "-" && first[4] != "restricted" && first[4] != "restricted,hidden" && first[4] != "sunlnk") return false;
			if(first[0].Length == 11 && first[0][10] == '@') {
				if(lines.Length != 2 || lines[1].Trim() != "com.apple.rootless\t  0") return false;
			} else if(first[0].Length != 10 || lines.Length != 1) return false;
			aclPresent = false;
			return true;
		} catch { return false; }
	}

	internal static uint CurrentUid() {
		if(!OperatingSystem.IsMacOS()) return 0;
		try {
			using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/id") {
				UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
			} };
			process.StartInfo.ArgumentList.Add("-u");
			if(!process.Start()) return 0;
			var output = process.StandardOutput.ReadToEnd().Trim();
			return process.WaitForExit(2000) && process.ExitCode == 0 && uint.TryParse(output, out var uid) ? uid : 0;
		} catch { return 0; }
	}

	internal static bool VerifyProtectedFile(string path, uint supervisorUid) {
		if(supervisorUid == 0 || !OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(path)) return false;
		try {
			var full = Path.GetFullPath(path);
			if(!Path.IsPathFullyQualified(full) || full.StartsWith("/tmp/", StringComparison.Ordinal) ||
				full.StartsWith("/private/tmp/", StringComparison.Ordinal)) return false;
			var component = Path.GetPathRoot(full)!;
			if(!TryStat(component, out var uid, out var mode, out var links, out var acl) ||
				!SafeProtectionRecord(uid, supervisorUid, mode, true, links, acl)) return false;
			var relative = full[component.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
			for(var i = 0; i < relative.Length; ++i) {
				component = Path.Combine(component, relative[i]);
				var isDir = i != relative.Length - 1;
				if(File.GetAttributes(component).HasFlag(FileAttributes.ReparsePoint) ||
					!TryStat(component, out uid, out mode, out links, out acl) ||
					!SafeProtectionRecord(uid, supervisorUid, mode, isDir, links, acl)) return false;
			}
			return relative.Length > 0 && File.Exists(full);
		} catch { return false; }
	}

	internal static bool VerifyProtectedInstall(string? releaseId, string releasePath, uint supervisorUid,
		out string holdReason) {
		holdReason = "HOLD_NO_SPAWN: unprotected release path";
		if(!IsAcceptedTarget(releaseId) || supervisorUid == 0 || !OperatingSystem.IsMacOS() ||
			string.IsNullOrWhiteSpace(releasePath)) return false;
		try {
			var root = Path.GetFullPath(releasePath);
			if(!Path.IsPathFullyQualified(root) || root.StartsWith("/tmp/", StringComparison.Ordinal) ||
				root.StartsWith("/private/tmp/", StringComparison.Ordinal)) return false;
			var component = Path.GetPathRoot(root)!;
			if(!TryStat(component, out var rootUid, out var rootMode, out var rootLinks, out var rootAcl) ||
				!SafeProtectionRecord(rootUid, supervisorUid, rootMode, true, rootLinks, rootAcl)) return false;
			foreach(var part in root[component.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
				component = Path.Combine(component, part);
				if(!Directory.Exists(component) || File.GetAttributes(component).HasFlag(FileAttributes.ReparsePoint) ||
					!TryStat(component, out var uid, out var mode, out var links, out var acl) ||
					!SafeProtectionRecord(uid, supervisorUid, mode, true, links, acl)) return false;
			}
			var pending = new Stack<string>(); pending.Push(root);
			while(pending.TryPop(out var dir)) {
				foreach(var entry in Directory.EnumerateFileSystemEntries(dir)) {
					var attrs = File.GetAttributes(entry);
					if(attrs.HasFlag(FileAttributes.ReparsePoint) ||
						!TryStat(entry, out var uid, out var mode, out var links, out var acl) ||
						!SafeProtectionRecord(uid, supervisorUid, mode, attrs.HasFlag(FileAttributes.Directory), links, acl))
						return false;
					if(attrs.HasFlag(FileAttributes.Directory)) pending.Push(entry);
				}
			}
			return true;
		} catch { return false; }
	}

	static bool RegularFile(string path) => File.Exists(path) &&
		!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

	public static bool Verify(string? releaseId, string releasePath, out string holdReason) {
		holdReason = "HOLD_NO_SPAWN";
		if(!IsAcceptedTarget(releaseId) || string.IsNullOrWhiteSpace(releasePath)) return false;
		try {
			var root = Path.GetFullPath(releasePath);
			// Do not trust an alias/reparse component leading to the release root.
			var component = Path.GetPathRoot(root)!;
			// macOS /tmp is a system-owned alias for /private/tmp. The release
			// guard already accepts that exact alias; do not accept arbitrary links.
			if(component == "/" && root.StartsWith("/tmp/", StringComparison.Ordinal)) {
				if(new DirectoryInfo("/tmp").LinkTarget != "private/tmp") return false;
				root = Path.GetFullPath("/private" + root);
			}
			foreach(var part in root[component.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
				component = Path.Combine(component, part);
				if(!Directory.Exists(component) || File.GetAttributes(component).HasFlag(FileAttributes.ReparsePoint)) return false;
			}
			using var manifestStream = typeof(StrictReleaseGate).Assembly.GetManifestResourceStream("Ainur.Strict18005e8.Files")
				?? throw new InvalidDataException("embedded manifest missing");
			using var bytes = new MemoryStream();
			manifestStream.CopyTo(bytes);
			if(!string.Equals(Convert.ToHexString(SHA256.HashData(bytes.ToArray())), ManifestSha256, StringComparison.Ordinal)) return false;
			bytes.Position = 0;
			var expected = JsonSerializer.Deserialize<List<PinnedFile>>(bytes)
				?? throw new InvalidDataException("embedded manifest invalid");
			if(expected.Count != 471 || expected.Select(x => x.relative_path).Distinct(StringComparer.Ordinal).Count() != 471) return false;
			var paths = new HashSet<string>(StringComparer.Ordinal);
			var directoriesExpected = new HashSet<string>(StringComparer.Ordinal);
			foreach(var file in expected) {
				var name = file.relative_path;
				if(string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains('\\') ||
					name.Split('/').Any(part => part is "" or "." or "..") ||
					file.size_bytes < 0 || file.sha256?.Length != 64) return false;
				var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
				var parent = Path.GetDirectoryName(path)!;
				while(parent.Length > root.Length) {
					if(!Directory.Exists(parent) || File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) return false;
					directoriesExpected.Add(parent);
					parent = Path.GetDirectoryName(parent)!;
				}
				if(!RegularFile(path) || new FileInfo(path).Length != file.size_bytes ||
					!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), file.sha256, StringComparison.Ordinal)) return false;
				paths.Add(path);
			}
			if(!expected.Any(x => x.relative_path == "Ainur.Core.dll" && x.sha256 == CoreSha256) ||
				!expected.Any(x => x.relative_path == "Ainur.Server.dll" && x.sha256 == ServerSha256) ||
				!expected.Any(x => x.relative_path == "Ainur.Server" && x.sha256 == ExecutableSha256) ||
				!expected.Any(x => x.relative_path == "release.json" && x.sha256 == ReleaseJsonSha256)) return false;
			// Enumerate one directory at a time: never recurse through a link or an
			// unapproved directory. The physical 471-file release has 113 directories.
			if(directoriesExpected.Count != 113) return false;
			var directories = 0;
			var toVisit = new Stack<string>();
			toVisit.Push(root);
			while(toVisit.TryPop(out var dir)) {
				foreach(var entry in Directory.EnumerateFileSystemEntries(dir)) {
					var attrs = File.GetAttributes(entry);
					if(attrs.HasFlag(FileAttributes.ReparsePoint)) return false;
					if(attrs.HasFlag(FileAttributes.Directory)) {
						if(!directoriesExpected.Contains(entry) || ++directories > 113) return false;
						toVisit.Push(entry);
					} else if(!paths.Contains(entry)) return false;
				}
			}
			if(directories != 113) return false;
			using var releaseDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "release.json")));
			if(!releaseDoc.RootElement.TryGetProperty("id", out var id) || id.GetString() != ReleaseId ||
				!releaseDoc.RootElement.TryGetProperty("revision", out var revision) || revision.GetString() != SourceCommit ||
				!releaseDoc.RootElement.TryGetProperty("dirty", out var dirty) || dirty.GetBoolean()) return false;
			holdReason = "ACCEPTED_STRICT_PAYLOAD";
			return true;
		} catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException or System.Security.SecurityException) {
			return false;
		}
	}
}
