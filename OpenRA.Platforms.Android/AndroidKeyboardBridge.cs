#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * Soft-keyboard bridge — Platforms must not reference the Android launcher assembly.
 */
#endregion

using System;

namespace OpenRA.Platforms.Android
{
	/// <summary>
	/// Launcher assigns <see cref="SetWanted"/> to show/hide the system IME
	/// when a text field holds keyboard focus.
	/// </summary>
	public static class AndroidKeyboardBridge
	{
		/// <summary>Argument: true = show soft keyboard, false = hide.</summary>
		public static Action<bool> SetWanted;
	}
}
