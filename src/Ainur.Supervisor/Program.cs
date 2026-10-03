using Ainur.Releasing;
using Ainur.Supervisor;

// ainur-supervisor run [--home DIR] [--port N] [--source REPO]
// ainur-supervisor build --source REPO [--home DIR]      HOLD_NO_SPAWN while only one strict payload is accepted
// ainur-supervisor activate RELEASE [--home DIR]          HOLD_NO_SPAWN until distinct rollback is attested
// ainur-supervisor status [--home DIR]
var command = args.FirstOrDefault() ?? "run";
var opts = SupervisorOptions.Parse(args.Skip(1).ToArray());
var releases = new Releases(opts.Home);

switch(command) {
	case "build": {
		Console.Error.WriteLine("HOLD_NO_SPAWN: source-build is disabled under single strict 18005e8 allowlist");
		return 3;
	}
	case "activate": {
		var id = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("release id required");
		if(!StrictReleaseGate.IsAcceptedTarget(id) || !StrictReleaseGate.Verify(id, releases.PathFor(id), out _)) {
			Console.Error.WriteLine("HOLD_NO_SPAWN: CLI activation target lacks exact independently accepted full-UI payload");
			return 3;
		}
		Console.Error.WriteLine("HOLD_NO_SPAWN: no independently accepted DISTINCT rollback; CLI activation disabled");
		return 3;
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
