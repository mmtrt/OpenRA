#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of the unofficial OpenRA Android port effort.
 * Available under the same GPL-3.0 terms as upstream OpenRA.
 */
#endregion

using System;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Android implementation of IPlatform.
	/// Mirrors DefaultPlatform but will eventually use Android-specific
	/// window, sound and font backends.
	/// </summary>
	public class AndroidPlatform : IPlatform
	{
		public IPlatformWindow CreateWindow(
			Size size, WindowMode windowMode, float scaleModifier,
			int vertexBatchSize, int indexBatchSize, int videoDisplay, GLProfile profile)
		{
			// Force Embedded (OpenGL ES 3.0+) on Android.
			// Desktop "Modern" / ANGLE profiles are not applicable.
			var effectiveProfile = profile == GLProfile.Automatic || profile == GLProfile.Embedded
				? GLProfile.Embedded
				: profile;

			return new AndroidPlatformWindow(size, windowMode, scaleModifier,
				vertexBatchSize, indexBatchSize, videoDisplay, effectiveProfile);
		}

		public ISoundEngine CreateSound(string device)
		{
			// TODO Phase 5: AAudio or OpenAL-soft Android backend
			// For now return a dummy so the engine can boot.
			return new AndroidDummySoundEngine();
		}

		public IFont CreateFont(byte[] data)
		{
			// TODO: FreeType Android build or system font fallback
			throw new NotImplementedException("Android font backend not yet implemented.");
		}
	}
}
