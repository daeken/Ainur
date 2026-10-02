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
	static extern int OpenNoFollow(string path, int flags);

	[SupportedOSPlatform("macos")]
	[SupportedOSPlatform("linux")]
	static byte[]? ReadKey(string home) {
		var path = Path.Combine(home, "supervisor", KeyName);
		try {
			var info = new FileInfo(path);
			if(!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
				info.UnixFileMode != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) return null;
			// Kernel O_NOFOLLOW prevents a swapped final symlink from being opened.
			const int oReadOnly = 0;
			var oNoFollow = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
			var descriptor = OpenNoFollow(path, oReadOnly | oNoFollow);
			if(descriptor < 0) return null;
			using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
			using var file = new FileStream(handle, FileAccess.Read);
			if(file.Length != 65 || File.GetUnixFileMode(file.SafeFileHandle) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) return null;
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
		var key = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? ReadKey(options.Home) : null;
		if(key is null) return Results.NotFound();
		// A Host header is not authentication; peer IP and a 256-bit out-of-band capability are required.
		if(ctx.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip) ||
			ctx.Request.QueryString.HasValue || ctx.Request.Headers.Authorization.Count != 1)
			return Results.StatusCode(StatusCodes.Status403Forbidden);
		var value = ctx.Request.Headers.Authorization.ToString();
		if(!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length != 71)
			return Results.StatusCode(StatusCodes.Status403Forbidden);
		var supplied = DecodeKey(value[7..]);
		if(supplied is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
		if(!CryptographicOperations.FixedTimeEquals(key, supplied)) return Results.StatusCode(StatusCodes.Status403Forbidden);
		return Results.Json(new {
			route_class = RouteClass((route ?? (() => Environment.GetEnvironmentVariable("AINUR_OPENAI_ROUTE")))()),
			process_id = Environment.ProcessId,
			process_started_utc = StartedUtc,
			release = options.Release ?? "dev",
			generation = runtime.Generation,
			provider_policy = ProviderPolicy
		});
	}
}
