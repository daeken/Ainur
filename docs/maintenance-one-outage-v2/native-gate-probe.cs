using Ainur.Supervisor;

// Offline-only probe. All mutations occur in a fresh /tmp copy; the accepted
// staged payload is read-only. Never run this fixture against a private home.
var root = args[0];
var caseName = args[1];
var release = StrictReleaseGate.ReleaseId;
string? scratch = null;
try {
    if(caseName is "tampered" or "extra" or "link" or "extra-directory") {
        scratch = "/tmp/ainur-native-gate-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(scratch);
        foreach(var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(root, entry);
            var dest = Path.Combine(scratch, relative);
            if(Directory.Exists(entry)) Directory.CreateDirectory(dest);
            else { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(entry, dest); }
        }
        root = scratch;
        if(caseName == "tampered") File.AppendAllText(Path.Combine(root, "release.json"), " ");
        if(caseName == "extra") File.WriteAllText(Path.Combine(root, "EXTRA"), "x");
        if(caseName == "link") File.CreateSymbolicLink(Path.Combine(root, "LINK"), "/tmp");
        if(caseName == "extra-directory") Directory.CreateDirectory(Path.Combine(root, "EXTRA"));
    }
    if(caseName == "old-e022") release = "r20261003-e022";
    if(caseName == "old-766") release = "r20261003-766";
    if(caseName == "old-461") release = "r20261003-461";
    if(caseName == "missing") root += "-missing";
    var ok = StrictReleaseGate.Verify(release, root, out var hold);
    var expected = caseName == "valid";
    Console.WriteLine($"{caseName}: result={ok} reason={hold} expected={expected} {(ok==expected ? "PASS" : "FAIL")}");
    if(ok!=expected || StrictReleaseGate.IsIndependentRollback(release, release)) return 1;
    return 0;
} finally { if(scratch is not null) Directory.Delete(scratch, recursive:true); }
