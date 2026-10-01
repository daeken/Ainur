using Ainur.Releasing;
using Ainur.Supervisor;

// ainur-supervisor run [--home DIR] [--port N] [--source REPO]
// ainur-supervisor build --source REPO [--home DIR]      builds an immutable release and prints its id
// ainur-supervisor activate RELEASE [--home DIR]          requests a supervised upgrade to RELEASE
// ainur-supervisor status [--home DIR]
var command = args.FirstOrDefault() ?? "run";
var opts = SupervisorOptions.Parse(args.Skip(1).ToArray());
var releases = new Releases(opts.Home);

switch(command) {
	case "build": {
		var source = opts.Source ?? throw new ArgumentException("--source is required");
		var id = await releases.BuildAsync(source, Console.Out);
		Console.WriteLine(id);
		return 0;
	}
	case "activate": {
		var id = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("release id required");
		UpgradeRequest.Write(opts.Home, new UpgradeRequest { ReleaseId = id, RequestedBy = "cli" });
		Console.WriteLine($"Upgrade to {id} requested.");
		return 0;
	}
	case "status": {
		Console.WriteLine(File.Exists(releases.StatePath) ? File.ReadAllText(releases.StatePath) : "{}");
		return 0;
	}
	case "run": {
		using var supervisor = new Supervisor(opts, releases);
		// SIGINT/SIGTERM stop the runtime cleanly before the supervisor exits, so no runtime is left orphaned.
		void OnSignal(System.Runtime.InteropServices.PosixSignalContext ctx) { ctx.Cancel = true; supervisor.RequestShutdown(); }
		using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, OnSignal);
		using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, OnSignal);
		return await supervisor.RunAsync();
	}
	default:
		Console.Error.WriteLine($"Unknown command {command}");
		return 2;
}
