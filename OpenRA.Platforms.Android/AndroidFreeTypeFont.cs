#region Copyright & License Information
/*
 * Based on OpenRA.Platforms.Default/FreeTypeFont.cs — Android DllImport uses "freetype".
 */
#endregion

using System;
using System.Runtime.InteropServices;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	static class FreeTypeNative
	{
		internal const uint OK = 0x00;
		internal const int FT_LOAD_RENDER = 0x04;

		internal const int MetricsWidthOffset = 0;
		internal const int BitmapPitchOffset = 8;
		internal static readonly int FaceRecGlyphOffset = IntPtr.Size == 8 ? 152 : 84;
		internal static readonly int GlyphSlotMetricsOffset = IntPtr.Size == 8 ? 48 : 24;
		internal static readonly int GlyphSlotBitmapOffset = IntPtr.Size == 8 ? 152 : 76;
		internal static readonly int GlyphSlotBitmapLeftOffset = IntPtr.Size == 8 ? 192 : 100;
		internal static readonly int GlyphSlotBitmapTopOffset = IntPtr.Size == 8 ? 196 : 104;
		internal static readonly int MetricsHeightOffset = IntPtr.Size == 8 ? 8 : 4;
		internal static readonly int MetricsAdvanceOffset = IntPtr.Size == 8 ? 32 : 16;
		internal static readonly int BitmapBufferOffset = IntPtr.Size == 8 ? 16 : 12;

		// Android NDK build ships libfreetype.so (not freetype6)
		[DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
		internal static extern uint FT_Init_FreeType(out IntPtr library);

		[DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
		internal static extern uint FT_New_Memory_Face(IntPtr library, IntPtr file_base, int file_size, int face_index, out IntPtr aface);

		[DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
		internal static extern uint FT_Done_Face(IntPtr face);

		[DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
		internal static extern uint FT_Set_Pixel_Sizes(IntPtr face, uint pixel_width, uint pixel_height);

		[DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
		internal static extern uint FT_Load_Char(IntPtr face, uint char_code, int load_flags);
	}

	public sealed class AndroidFreeTypeFont : IFont
	{
		static readonly FontGlyph EmptyGlyph = new()
		{
			Offset = int2.Zero,
			Size = new Size(0, 0),
			Advance = 0,
			Data = null
		};

		static IntPtr library = IntPtr.Zero;
		static readonly object LibraryLock = new();

		readonly GCHandle faceHandle;
		readonly IntPtr face;
		bool disposed;

		public AndroidFreeTypeFont(byte[] data)
		{
			lock (LibraryLock)
			{
				if (library == IntPtr.Zero)
				{
					var err = FreeTypeNative.FT_Init_FreeType(out library);
					if (err != FreeTypeNative.OK || library == IntPtr.Zero)
						throw new InvalidOperationException("FT_Init_FreeType failed: " + err);
				}
			}

			faceHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
			var err2 = FreeTypeNative.FT_New_Memory_Face(library, faceHandle.AddrOfPinnedObject(), data.Length, 0, out face);
			if (err2 != FreeTypeNative.OK || face == IntPtr.Zero)
			{
				if (faceHandle.IsAllocated)
					faceHandle.Free();
				throw new InvalidOperationException("FT_New_Memory_Face failed: " + err2);
			}
		}

		public FontGlyph CreateGlyph(char c, int size, float deviceScale)
		{
			var scaledSize = (uint)Math.Max(1, (int)(size * deviceScale));
			if (FreeTypeNative.FT_Set_Pixel_Sizes(face, scaledSize, scaledSize) != FreeTypeNative.OK)
				return EmptyGlyph;

			if (FreeTypeNative.FT_Load_Char(face, c, FreeTypeNative.FT_LOAD_RENDER) != FreeTypeNative.OK)
				return EmptyGlyph;

			var glyph = Marshal.ReadIntPtr(IntPtr.Add(face, FreeTypeNative.FaceRecGlyphOffset));
			if (glyph == IntPtr.Zero)
				return EmptyGlyph;

			var metrics = IntPtr.Add(glyph, FreeTypeNative.GlyphSlotMetricsOffset);
			var metricsWidth = Marshal.ReadIntPtr(IntPtr.Add(metrics, FreeTypeNative.MetricsWidthOffset));
			var metricsHeight = Marshal.ReadIntPtr(IntPtr.Add(metrics, FreeTypeNative.MetricsHeightOffset));
			var metricsAdvance = Marshal.ReadIntPtr(IntPtr.Add(metrics, FreeTypeNative.MetricsAdvanceOffset));

			var bitmap = IntPtr.Add(glyph, FreeTypeNative.GlyphSlotBitmapOffset);
			var bitmapPitch = Marshal.ReadInt32(IntPtr.Add(bitmap, FreeTypeNative.BitmapPitchOffset));
			var bitmapBuffer = Marshal.ReadIntPtr(IntPtr.Add(bitmap, FreeTypeNative.BitmapBufferOffset));

			var bitmapLeft = Marshal.ReadInt32(IntPtr.Add(glyph, FreeTypeNative.GlyphSlotBitmapLeftOffset));
			var bitmapTop = Marshal.ReadInt32(IntPtr.Add(glyph, FreeTypeNative.GlyphSlotBitmapTopOffset));

			var glyphSize = new Size((int)metricsWidth >> 6, (int)metricsHeight >> 6);
			var glyphAdvance = (int)metricsAdvance >> 6;

			if (glyphSize.Width <= 0 || glyphSize.Height <= 0 || bitmapBuffer == IntPtr.Zero)
			{
				// Data MUST be a non-null (even if empty) array here, not null. The shared
				// cross-platform caller (OpenRA.Game/Graphics/SpriteFont.cs CreateGlyph)
				// treats glyph.Data == null as "this glyph failed to load" and forces
				// Advance to 0, discarding whatever Advance value we return below. Every
				// invisible-but-still-advancing glyph — most importantly the space
				// character — hits this exact branch (zero-size bitmap), so returning
				// Data = null here silently collapsed every space to zero width: words
				// rendered with correct internal letter spacing but ran together with no
				// gaps between them. The desktop reference implementation
				// (OpenRA.Platforms.Default/FreeTypeFont.cs) never has this problem
				// because it never special-cases the empty-bitmap case at all — it always
				// returns Data = new byte[width*height], which for a 0x0 glyph is simply
				// a non-null empty array, so the shared caller takes its normal path and
				// uses the real Advance value.
				return new FontGlyph
				{
					Offset = new int2(bitmapLeft, -bitmapTop),
					Size = new Size(0, 0),
					Advance = glyphAdvance,
					Data = Array.Empty<byte>()
				};
			}

			// Guard absurd sizes from bad FT struct offsets
			if (glyphSize.Width > 512 || glyphSize.Height > 512)
				return EmptyGlyph;

			var g = new FontGlyph
			{
				Advance = glyphAdvance,
				Offset = new int2(bitmapLeft, -bitmapTop),
				Size = glyphSize,
				Data = new byte[glyphSize.Width * glyphSize.Height]
			};

			unsafe
			{
				var p = (byte*)bitmapBuffer;
				var k = 0;
				for (var j = 0; j < glyphSize.Height; j++)
				{
					for (var i = 0; i < glyphSize.Width; i++)
						g.Data[k++] = p[i];
					p += bitmapPitch;
				}
			}

			return g;
		}

		public void Dispose()
		{
			if (!disposed)
			{
				if (face != IntPtr.Zero)
					FreeTypeNative.FT_Done_Face(face);
				if (faceHandle.IsAllocated)
					faceHandle.Free();
				disposed = true;
			}
		}
	}
}