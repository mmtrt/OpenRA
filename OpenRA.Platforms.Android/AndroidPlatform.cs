#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port.
 */
#endregion

using System;
using OpenRA.Primitives;
using ALog = global::Android.Util.Log;

namespace OpenRA.Platforms.Android
{
	public class AndroidPlatform : IPlatform
	{
		public IPlatformWindow CreateWindow(
			Size size, WindowMode windowMode, float scaleModifier,
			int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			var effectiveProfile = profile == GLProfile.Automatic || profile == GLProfile.Embedded
				? GLProfile.Embedded
				: profile;

			return new AndroidPlatformWindow(size, windowMode, scaleModifier,
				vertexBatchSize, indexBatchSize, videoDisplay, effectiveProfile);
		}

		public ISoundEngine CreateSound(string device)
		{
			return new AndroidDummySoundEngine();
		}

		public IFont CreateFont(byte[] data)
		{
			try
			{
				return new AndroidFreeTypeFont(data);
			}
			catch (Exception e)
			{
				ALog.Error("OpenRA.Font", "FreeType font failed: " + e);
				// Safe empty glyphs (Data=null) so SpriteFont skips blit
				return new AndroidSafeEmptyFont();
			}
		}
	}

	/// <summary>Fallback that never returns non-null empty Data with positive Size.</summary>
	sealed class AndroidSafeEmptyFont : IFont
	{
		public FontGlyph CreateGlyph(char c, int size, float deviceScale)
		{
			return new FontGlyph
			{
				Offset = int2.Zero,
				Size = new Size(0, 0),
				Advance = size * 0.5f * deviceScale,
				Data = null
			};
		}

		public void Dispose() { }
	}
}
