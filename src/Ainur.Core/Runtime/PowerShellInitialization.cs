using System.Management.Automation.Runspaces;

namespace Ainur.Core.Runtime;

/// <summary>Shared initialization fence for session and workbook runspaces; pipelines remain concurrent.</summary>
internal static class PowerShellInitialization {
	static readonly Lock OpenGate = new();

	public static void Open(Runspace runspace) {
		// Default filesystem drives call DriveInfo.GetDrives -> getmntinfo on macOS. The native mount
		// buffer is shared, so overlapping opens can corrupt drive roots or crash during enumeration.
		lock(OpenGate) runspace.Open();
	}
}
