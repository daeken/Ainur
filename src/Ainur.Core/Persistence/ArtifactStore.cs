using System.Text;

namespace Ainur.Core.Persistence;

/// <summary>Content-addressed immutable storage for large payloads (tool results, raw provider exchanges).</summary>
public sealed class ArtifactStore(string root) {
	public readonly string Root = root;

	public string Put(string text) => Put(Encoding.UTF8.GetBytes(text));

	public string Put(byte[] data) {
		var hash = Hash.Sha256(data);
		var path = PathFor(hash);
		if(!File.Exists(path)) {
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllBytes(tmp, data);
			File.Move(tmp, path, overwrite: true);
		}
		return "sha256:" + hash;
	}

	public string GetText(string reference) => Encoding.UTF8.GetString(Get(reference));

	public byte[] Get(string reference) {
		if(!reference.StartsWith("sha256:")) throw new ArgumentException($"Unknown artifact reference {reference}");
		return File.ReadAllBytes(PathFor(reference[7..]));
	}

	public bool Exists(string reference) => reference.StartsWith("sha256:") && File.Exists(PathFor(reference[7..]));

	string PathFor(string hash) => System.IO.Path.Combine(Root, hash[..2], hash[2..]);
}
