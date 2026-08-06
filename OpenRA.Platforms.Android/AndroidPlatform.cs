#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Unofficial OpenRA Android port.
 */
#endregion

using OpenRA.Primitives;

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
			// Stub glyphs until FreeType is wired
			return new AndroidStubFont();
		}
	}
}
