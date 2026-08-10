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
	/// Launcher assigns callbacks to show/hide the system IME when a text field is used.
	/// </summary>
	public static class AndroidKeyboardBridge
	{
		/// <summary>Argument: true = show soft keyboard, false = hide.</summary>
		public static Action<bool> SetWanted;

		/// <summary>
		/// Fired when a mouse-down lands inside the focused TextField bounds.
		/// Launcher uses this to open the IME (chat / name fields) without opening on random taps.
		/// </summary>
		public static Action TextFieldTapped;
	}
}
