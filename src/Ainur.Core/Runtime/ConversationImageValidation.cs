using System.Buffers.Binary;
using Ainur.Core.Persistence;
using SkiaSharp;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Ainur.Core.Runtime;

/// <summary>Bounded, fully decoded PNG input; never loads files or URLs supplied by a caller.</summary>
public static class ConversationImageValidation {
	public const int MaxBytes = 2 * 1024 * 1024;
	public const int MaxDimension = 2048;
	public const int MaxPixels = 3_000_000;

	public static (int Width, int Height) Validate(byte[] bytes, string mimeType) {
		if(mimeType != "image/png") throw new DomainException("Only PNG images (image/png) are supported; JPEG, WebP, SVG and HTML are not accepted.");
		if(bytes.Length is < 45 or > MaxBytes) throw new DomainException("PNG image must be nonempty and at most 2 MiB.");
		ReadOnlySpan<byte> data = bytes;
		if(!data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new DomainException("Image signature is not PNG.");
		// Validate chunk framing and CRCs before the native decoder; reject animation and trailing data.
		var offset = 8;
		var ended = false;
		using var compressed = new MemoryStream();
		var sawIdat = false; var endedIdat = false;
		while(offset < data.Length) {
			if(data.Length - offset < 12) throw new DomainException("Truncated PNG chunk.");
			var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
			if(length > (uint) (data.Length - offset - 12)) throw new DomainException("Truncated PNG chunk.");
			var type = data.Slice(offset + 4, 4);
			if(offset == 8 && (!type.SequenceEqual("IHDR"u8) || length != 13)) throw new DomainException("PNG requires a valid first IHDR chunk.");
			if(type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) throw new DomainException("Animated or multi-frame PNG images are not supported.");
			if(type.SequenceEqual("iCCP"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8)) throw new DomainException("PNG compressed profile/text metadata is not supported.");
			var crc = uint.MaxValue;
			foreach(var b in data.Slice(offset + 4, (int) length + 4)) {
				crc ^= b;
				for(var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint) -(int) (crc & 1));
			}
			if(~crc != BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + (int) length, 4))) throw new DomainException("PNG chunk checksum is invalid.");
			if(type.SequenceEqual("IDAT"u8)) {
				if(endedIdat) throw new DomainException("PNG IDAT chunks must be consecutive.");
				sawIdat = true;
				compressed.Write(data.Slice(offset + 8, (int) length));
			} else if(sawIdat) endedIdat = true;
			offset += (int) length + 12;
			if(type.SequenceEqual("IEND"u8)) {
				if(length != 0 || offset != data.Length) throw new DomainException("PNG has trailing data or invalid IEND.");
				ended = true; break;
			}
		}
		if(!ended) throw new DomainException("PNG is missing IEND.");
		var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
		var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
		if(width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || (long) width * height > MaxPixels)
			throw new DomainException("PNG dimensions exceed 2048 per edge or 3,000,000 decoded pixels.");
		// SKCodec may ignore bytes after the zlib member. Validate framing independently with
		// an inflater exposing stream-end AND unused-input (ZLibStream exposes neither).
		// Skia also tolerates surplus *inflated* bytes inside one valid zlib member. Require
		// exact scanline accounting, including packed samples, row padding and empty Adam7 passes.
		var expectedBytes = ExpectedScanlineBytes(width, height, data[24], data[25], data[26], data[27], data[28]);
		try {
			var inflater = new Inflater(); // zlib wrapper required, including Adler-32 checksum
			inflater.SetInput(compressed.ToArray());
			var buffer = new byte[8192]; long inflated = 0;
			while(!inflater.IsFinished) {
				var count = inflater.Inflate(buffer);
				inflated += count;
				if(inflated > expectedBytes) throw new DomainException("PNG inflated pixel stream has surplus scanline data.");
				if(count == 0 && !inflater.IsFinished) throw new DomainException("PNG compressed pixel stream is incomplete or requires a dictionary.");
			}
			if(inflater.RemainingInput != 0) throw new DomainException("PNG compressed pixel stream has trailing payload.");
			if(inflated != expectedBytes) throw new DomainException("PNG inflated pixel stream has incomplete scanline data.");
		} catch(SharpZipBaseException) { throw new DomainException("PNG compressed pixel stream or checksum is invalid."); }
		using var encoded = SKData.CreateCopy(bytes);
		using var codec = SKCodec.Create(encoded);
		if(codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1 || codec.Info.Width != width || codec.Info.Height != height)
			throw new DomainException("PNG could not be decoded as a single image.");
		using var bitmap = new SKBitmap(new SKImageInfo((int) width, (int) height, SKColorType.Rgba8888, SKAlphaType.Premul));
		if(codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new DomainException("PNG pixel data is invalid or truncated.");
		return ((int) width, (int) height);
	}

	static long ExpectedScanlineBytes(uint width, uint height, byte depth, byte color, byte compression, byte filter, byte interlace) {
		var channels = color switch {
			0 when depth is 1 or 2 or 4 or 8 or 16 => 1,
			2 when depth is 8 or 16 => 3,
			3 when depth is 1 or 2 or 4 or 8 => 1,
			4 when depth is 8 or 16 => 2,
			6 when depth is 8 or 16 => 4,
			_ => throw new DomainException("PNG color type or bit depth is invalid."),
		};
		if(compression != 0 || filter != 0 || interlace > 1) throw new DomainException("PNG compression, filter or interlace method is invalid.");
		checked {
			var bitsPerPixel = channels * depth;
			long Pass(int x, int y, int dx, int dy) {
				if(width <= x || height <= y) return 0; // No pixels means no filter byte either.
				var columns = ((long) width - x + dx - 1) / dx;
				var rows = ((long) height - y + dy - 1) / dy;
				return checked(rows * (1 + (columns * bitsPerPixel + 7) / 8));
			}
			if(interlace == 0) return Pass(0, 0, 1, 1);
			return Pass(0, 0, 8, 8) + Pass(4, 0, 8, 8) + Pass(0, 4, 4, 8) + Pass(2, 0, 4, 4)
				+ Pass(0, 2, 2, 4) + Pass(1, 0, 2, 2) + Pass(0, 1, 1, 2);
		}
	}
}
