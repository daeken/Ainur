using System.Buffers.Binary;
using Ainur.Core.Persistence;
using SkiaSharp;

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
		using var encoded = SKData.CreateCopy(bytes);
		using var codec = SKCodec.Create(encoded);
		if(codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1 || codec.Info.Width != width || codec.Info.Height != height)
			throw new DomainException("PNG could not be decoded as a single image.");
		using var bitmap = new SKBitmap(new SKImageInfo((int) width, (int) height, SKColorType.Rgba8888, SKAlphaType.Premul));
		if(codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new DomainException("PNG pixel data is invalid or truncated.");
		return ((int) width, (int) height);
	}
}
