using Ainur.Core.Tools;

namespace Ainur.Tests;

public class MultiEditTests {
	[Fact]
	public void AppliesSequentialEdits() {
		var r = MultiEdit.Apply("a\nb\nc\n", [new("a", "A"), new("A\nb", "AB")], null);
		Assert.Empty(r.Errors);
		Assert.Equal("AB\nc\n", r.Text);
		Assert.Equal(2, r.Applied.Count);
	}

	[Fact]
	public void OneMissingChunkFailsEverything() {
		var r = MultiEdit.Apply("alpha\nbeta\n", [new("alpha", "ALPHA"), new("gamma", "GAMMA"), new("beta", "BETA")], "tail");
		Assert.Null(r.Text);
		Assert.Single(r.Errors);
		Assert.Contains("edit 2", r.Errors[0]);
	}

	[Fact]
	public void ReportsEveryFailure() {
		var r = MultiEdit.Apply("x x\n", [new("y", "z"), new("x", "q"), new("", "e")], null);
		Assert.Equal(3, r.Errors.Count);
		Assert.Contains("matches 2 locations", r.Errors[1]);
		Assert.Contains("empty", r.Errors[2]);
	}

	[Fact]
	public void ReplaceAllAndAppend() {
		var r = MultiEdit.Apply("x x", [new("x", "y", true)], "z\n");
		Assert.Equal("y y\nz\n", r.Text);
	}

	[Fact]
	public void HintsAtWhitespaceNearMiss() {
		var r = MultiEdit.Apply("    if(a) {\n        b();\n    }\n", [new("if(a) {\n    b();\n}", "x")], null);
		Assert.Contains("whitespace", r.Errors[0]);
	}

	[Fact]
	public void HintsAtLineEndingMismatch() {
		var r = MultiEdit.Apply("a\r\nb\r\n", [new("a\nb", "x")], null);
		Assert.Contains("line endings", r.Errors[0]);
	}
}
