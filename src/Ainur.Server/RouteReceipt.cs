using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Ainur.Core.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32.SafeHandles;

namespace Ainur.Server;

/// <summary>Operator-only, process-local route classification. Does not read credentials or invoke providers.</summary>
public static class RouteReceipt {
	public const string ProviderPolicy = "openai_subscription_strict";
	const string KeyName = "route-receipt.key";
	static readonly DateTimeOffset StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

	public static string RouteClass(string? route) => route is null ? "unset" : route.Trim().ToLowerInvariant() switch {
		"subscription" => "subscription",
		"api" => "api",
		"auto" => "auto",
		_ => "other"
	};

	[DllImport("libc", SetLastError = true, EntryPoint = "open")]
	static extern int OpenDirectory(string path, int flags);
	[DllImport("libc", SetLastError = true, EntryPoint = "openat")]
	static extern int OpenKey(int directory, string name, int flags);
	[DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
	static extern int Stat(int descriptor, [Out] byte[] metadata);
	[DllImport("libc", EntryPoint = "getuid")]
	static extern uint Uid();

	// Native stat layouts on macOS arm64/x64 and Linux arm64/x64 respectively.
	// Inspect the *opened* descriptor before any read, including FIFO/special files.
	static bool OwnedFile(int fd, bool directory, bool assembly = false, bool publicDirectory = false) {
		var stat = new byte[256];
		if(Stat(fd, stat) != 0) return false;
		var mac = OperatingSystem.IsMacOS();
		var mode = mac ? BitConverter.ToUInt16(stat, 4) : BitConverter.ToUInt32(stat, 24);
		var links = mac ? BitConverter.ToUInt16(stat, 6) : BitConverter.ToUInt64(stat, 16);
		var owner = BitConverter.ToUInt32(stat, mac ? 16 : 28);
		var kind = mode & 0xF000;
		return owner == Uid() && kind == (directory ? 0x4000 : 0x8000) && (directory || links == 1) &&
			(directory ? (mode & 0x1C0) == 0x1C0 &&
				(publicDirectory ? (mode & 0x12) == 0 : (mode & 0x3F) == 0) :
				assembly || (mode & 0x1FF) == 0x180);
	}

	[SupportedOSPlatform("macos")]
	[SupportedOSPlatform("linux")]
	static string? CoreHash() {
		try {
			// Use the loaded Core assembly, never a request path or release metadata. The controller
			// separately hashes the pinned release file and compares this receipt with it.
			var location = typeof(AinurRuntime).Assembly.Location;
			if(string.IsNullOrEmpty(location)) return null;
			var mac = OperatingSystem.IsMacOS();
			var fd = OpenDirectory(location, mac ? 0x100 | 0x4 | 0x1000000 : 0x20000 | 0x800 | 0x80000);
			if(fd < 0) return null;
			using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
			if(!OwnedFile(fd, directory: false, assembly: true)) return null;
			using var file = new FileStream(handle, FileAccess.Read);
			if(file.Length <= 0 || file.Length > 64 * 1024 * 1024) return null;
			return Convert.ToHexString(SHA256.HashData(file));
		} catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			return null;
		}
	}

	[SupportedOSPlatform("macos")]
	[SupportedOSPlatform("linux")]
	static byte[]? ReadKey(string home) {
		try {
			var mac = OperatingSystem.IsMacOS();
			// O_DIRECTORY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC; openat limits final-key lookup to verified parent.
			var noFollow = mac ? 0x100 : 0x20000;
			var nonblock = mac ? 0x4 : 0x800;
			var closeExec = mac ? 0x1000000 : 0x80000;
			var directory = mac ? 0x100000 : 0x10000;
			// Open each component relative to the previous verified descriptor; no intermediate
			// symlink or permissive supervisor/secret directory can redirect key lookup.
			var homeFd = OpenDirectory(home, noFollow | nonblock | closeExec | directory);
			if(homeFd < 0) return null;
			using var homeHandle = new SafeFileHandle((nint)homeFd, ownsHandle: true);
			if(!OwnedFile(homeFd, directory: true, publicDirectory: true)) return null;
			var supervisorFd = OpenKey(homeFd, "supervisor", noFollow | nonblock | closeExec | directory);
			if(supervisorFd < 0) return null;
			using var supervisorHandle = new SafeFileHandle((nint)supervisorFd, ownsHandle: true);
			if(!OwnedFile(supervisorFd, directory: true, publicDirectory: true)) return null;
			var secretFd = OpenKey(supervisorFd, "receipt-secrets", noFollow | nonblock | closeExec | directory);
			if(secretFd < 0) return null;
			using var secretHandle = new SafeFileHandle((nint)secretFd, ownsHandle: true);
			if(!OwnedFile(secretFd, directory: true)) return null;
			var keyFd = OpenKey(secretFd, KeyName, noFollow | nonblock | closeExec);
			if(keyFd < 0) return null;
			using var key = new SafeFileHandle((nint)keyFd, ownsHandle: true);
			if(!OwnedFile(keyFd, directory: false)) return null;
			using var file = new FileStream(key, FileAccess.Read);
			if(file.Length != 65) return null;
			var text = new byte[65];
			file.ReadExactly(text);
			if(text[64] != (byte)'\n') return null;
			return DecodeKey(Encoding.ASCII.GetString(text, 0, 64));
		} catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			return null;
		}
	}

	static byte[]? DecodeKey(string text) {
		if(text.Length != 64 || text.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
			return null;
		return Convert.FromHexString(text);
	}

	public static IResult Get(HttpContext ctx, ServerOptions options, AinurRuntime runtime, Func<string?>? route = null) {
		ctx.Response.Headers.CacheControl = "no-store";
		// A Host header is not authentication; reject untrusted requests before opening even the key path.
		if(ctx.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip) ||
			ctx.Request.QueryString.HasValue || ctx.Request.Headers.Authorization.Count != 1)
			return Results.StatusCode(StatusCodes.Status403Forbidden);
		var value = ctx.Request.Headers.Authorization.ToString();
		if(!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length != 71)
			return Results.StatusCode(StatusCodes.Status403Forbidden);
		var supplied = DecodeKey(value[7..]);
		if(supplied is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
		var key = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? ReadKey(options.Home) : null;
		if(key is null) return Results.NotFound();
		if(!CryptographicOperations.FixedTimeEquals(key, supplied)) return Results.StatusCode(StatusCodes.Status403Forbidden);
		var coreHash = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? CoreHash() : null;
		if(coreHash is null) return Results.NotFound();
		return Results.Json(new {
			route_class = RouteClass((route ?? (() => Environment.GetEnvironmentVariable("AINUR_OPENAI_ROUTE")))()),
			process_id = Environment.ProcessId,
			process_started_utc = StartedUtc,
			release = options.Release ?? "dev",
			generation = runtime.Generation,
			provider_policy = ProviderPolicy,
			core_sha256 = coreHash
		});
	}
}
