using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

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

	// Child launch has an explicit allowlist, not an inherited environment with
	// selected entries overwritten. This includes .NET host and native loader
	// configuration: no DOTNET_*, CORECLR_*, COMPlus_*, DYLD_*, LD_*,
	// ASPNETCORE_*, or provider credential/paid-route override can pass through.
	// Only values independently pinned here enter the runtime child. No native
	// launch is enabled unless the other content/install/owner gates also pass.
	internal static void SetStrictChildEnvironment(ProcessStartInfo start) {
		start.Environment.Clear();
		start.Environment["AINUR_OPENAI_ROUTE"] = "subscription";
		start.Environment["AINUR_SUPERVISED"] = "1";
		start.Environment["HOME"] = "/Users/daeken";
		start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
		start.Environment["LANG"] = "C";
	}

	// Local OS, authorized operator and service UID are trusted; a hostile
	// same-UID local replacement is OUTSIDE the accepted threat model. Check
	// exact content independently immediately before spawn, and reject links,
	// ACL delegation and other-UID/group/world-writable release components.
	// The writable home/state remain necessary; this does not freeze them.
	// No install, repair or replacement is performed by the gate.
	internal static bool SafeProtectionRecord(uint ownerUid, uint supervisorUid, UnixFileMode mode,
		bool isDirectory, uint hardLinks, bool aclPresent) {
		const UnixFileMode writable = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
		if(supervisorUid == 0 || (ownerUid != 0 && ownerUid != supervisorUid) || aclPresent || (mode & writable) != 0) return false;
		return isDirectory ? hardLinks >= 1 : hardLinks == 1;
	}

	// Bounded exact system probe. Asynchronous pipe drain prevents a child that
	// writes into its pipe from blocking our timed wait. On timeout only the
	// exact child started here is killed; no unrelated process/tree is targeted.
	static bool TrySystemProbe(string executable, string[] arguments, out string output) {
		output = "";
		try {
			using var process = new Process { StartInfo = new ProcessStartInfo(executable) {
				UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false,
			} };
			process.StartInfo.Environment.Clear();
			foreach(var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
			if(!process.Start()) return false;
			try {
				var read = process.StandardOutput.ReadToEndAsync();
				if(!process.WaitForExit(2000) || !read.Wait(500)) return false;
				output = read.Result;
				return process.ExitCode == 0 && output.Length <= 8192;
			} finally {
				if(!process.HasExited) {
					try { process.Kill(); process.WaitForExit(1000); } catch { /* fail closed; only exact probe child targeted */ }
				}
			}
		} catch { return false; }
	}

	// Darwin-specific protection proof. Never claim a protected installation on
	// another OS or when stat/ACL inspection is unavailable or ambiguous.
	// stat reports owner, raw mode, hard-link count; ls -ldeO@ reports macOS ACL
	// and file flags (+ marks ACL). Unknown output is fail-closed.
	static bool TryStat(string path, out uint uid, out UnixFileMode mode, out uint links, out bool aclPresent) {
		uid = links = 0; aclPresent = true; mode = default;
		if(!OperatingSystem.IsMacOS()) return false;
		try {
			if(!TrySystemProbe("/usr/bin/stat", ["-f", "%u|%p|%l", path], out var statOutput)) return false;
			var line = statOutput.Trim();
			var parts = line.Split('|');
			if(parts.Length != 3 || !uint.TryParse(parts[0], out uid) ||
				!uint.TryParse(parts[2], out links)) return false;
			var rawMode = Convert.ToUInt32(parts[1], 8);
			mode = (UnixFileMode) (rawMode & 0xFFF);
			if(!TrySystemProbe("/bin/ls", ["-ldeO@", path], out var lsOutput)) return false;
			var detail = lsOutput.TrimEnd();
			var lines = detail.Split('\n');
			var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if(first.Length < 7 || first[0].Length is not (10 or 11) ||
				first[0][0] is not ('-' or 'd') || !uint.TryParse(first[1], out var lsLinks) ||
				lsLinks != links) return false;
			// Apple SIP's restricted/hidden flags on system-owned /usr do not
			// delegate write access; reject all other unknown flags. SIP's xattr
			// macOS rootless (SIP) and provenance do not delegate write access.
			// Any other extended attribute remains an explicit fail-closed case.
			if(first[4] != "-" && first[4] != "restricted" && first[4] != "restricted,hidden" && first[4] != "sunlnk" && first[4] != "sunlnk,hidden") return false;
			if(first[0].Length == 11 && first[0][10] == '@') {
				if(lines.Length != 2 || (lines[1].Trim() != "com.apple.rootless\t  0" &&
					lines[1].Trim() != "com.apple.provenance\t    11")) return false;
			} else if(first[0].Length != 10 || lines.Length != 1) return false;
			aclPresent = false;
			return true;
		} catch { return false; }
	}

	internal static uint CurrentUid() {
		if(!OperatingSystem.IsMacOS()) return 0;
		return TrySystemProbe("/usr/bin/id", ["-u"], out var output) && uint.TryParse(output.Trim(), out var uid) ? uid : 0;
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

	internal static bool VerifyControlledInstall(string? releaseId, string releasePath, uint supervisorUid,
		out string holdReason) {
		holdReason = "HOLD_NO_SPAWN: release install ownership/mode or content not controlled";
		if(!IsAcceptedTarget(releaseId) || supervisorUid == 0 || !OperatingSystem.IsMacOS() ||
			string.IsNullOrWhiteSpace(releasePath)) return false;
		try {
			var root = Path.GetFullPath(releasePath);
			if(!Path.IsPathFullyQualified(root)) return false;
			// macOS /tmp is an OS-owned alias for /private/tmp. This EXACT
			// sticky system ancestor permits physically isolated rehearsals;
			// no other group/world-writable release component is accepted.
			if(root.StartsWith("/tmp/", StringComparison.Ordinal)) {
				if(new DirectoryInfo("/tmp").LinkTarget != "private/tmp") return false;
				root = Path.GetFullPath("/private" + root);
			}
			var component = Path.GetPathRoot(root)!;
			if(!TryStat(component, out var rootUid, out var rootMode, out var rootLinks, out var rootAcl) ||
				!SafeProtectionRecord(rootUid, supervisorUid, rootMode, true, rootLinks, rootAcl)) return false;
			foreach(var part in root[component.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
				component = Path.Combine(component, part);
				if(!Directory.Exists(component) || File.GetAttributes(component).HasFlag(FileAttributes.ReparsePoint) ||
					!TryStat(component, out var uid, out var mode, out var links, out var acl)) return false;
				if(component == "/private/tmp") {
					var sticky = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
						UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
						UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit;
					if(uid != 0 || acl || mode != sticky || links < 1) return false;
				} else if(!SafeProtectionRecord(uid, supervisorUid, mode, true, links, acl)) return false;
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
