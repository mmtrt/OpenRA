// Soft keyboard for OpenRA TextFieldWidget focus on Android.
// NOTE: This file lives in namespace OpenRA.Android — never write "Android.Widget"
// or "Android.Text" unqualified; the compiler resolves them as OpenRA.Android.*.
// Always use global::Android.* or the aliases below.

using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using OpenRA.Platforms.Android;
using AKeycode = global::Android.Views.Keycode;
using AViewStates = global::Android.Views.ViewStates;
using AEditText = global::Android.Widget.EditText;
using AFrameLayout = global::Android.Widget.FrameLayout;
using AInputTypes = global::Android.Text.InputTypes;
using ATextChangedEventArgs = global::Android.Text.TextChangedEventArgs;

namespace OpenRA.Android
{
	/// <summary>
	/// Shows the system IME when a text field has keyboard focus.
	/// Characters are forwarded into <see cref="AndroidInput"/> key queue.
	/// </summary>
	public static class AndroidSoftKeyboard
	{
		static Activity activity;
		static AEditText hiddenInput;
		static bool wanted;
		static bool visible;
		static string lastText = "";
		static bool suppressChange;

		public static void Attach(Activity act, AFrameLayout root)
		{
			activity = act;
			if (hiddenInput != null)
				return;

			hiddenInput = new AEditText(act)
			{
				Focusable = true,
				FocusableInTouchMode = true,
				Visibility = AViewStates.Invisible,
				ImeOptions = ImeAction.Done,
				InputType = AInputTypes.ClassText | AInputTypes.TextFlagNoSuggestions
			};
			// Keep off-screen / zero size so it never covers GL
			var lp = new AFrameLayout.LayoutParams(1, 1)
			{
				LeftMargin = -1000,
				TopMargin = -1000
			};
			root.AddView(hiddenInput, lp);

			hiddenInput.TextChanged += OnTextChanged;
			hiddenInput.EditorAction += (s, e) =>
			{
				if (e.ActionId == ImeAction.Done || e.ActionId == ImeAction.Go || e.ActionId == ImeAction.Send)
				{
					EnqueueSpecial(AKeycode.Enter);
					Hide();
					e.Handled = true;
				}
			};

			hiddenInput.KeyPress += (s, e) =>
			{
				if (e.Event == null)
					return;
				if (e.Event.KeyCode == AKeycode.Del && e.Event.Action == KeyEventActions.Down)
				{
					EnqueueBackspace();
					e.Handled = true;
				}
				else if ((e.Event.KeyCode == AKeycode.Enter || e.Event.KeyCode == AKeycode.NumpadEnter)
				         && e.Event.Action == KeyEventActions.Down)
				{
					EnqueueSpecial(AKeycode.Enter);
					e.Handled = true;
				}
			};
		}

		static void OnTextChanged(object sender, ATextChangedEventArgs e)
		{
			if (suppressChange || hiddenInput == null)
				return;
			try
			{
				var text = hiddenInput.Text ?? "";
				if (text.Length > lastText.Length)
				{
					var added = text.Substring(lastText.Length);
					EnqueueChars(added);
				}
				else if (text.Length < lastText.Length)
				{
					var removed = lastText.Length - text.Length;
					for (var i = 0; i < removed; i++)
						EnqueueBackspace();
				}
				lastText = text;
			}
			catch (Exception ex)
			{
				AndroidFileLog.Warn("OpenRA.Keyboard", "TextChanged: " + ex.Message);
			}
		}

		static void EnqueueChars(string s)
		{
			var input = AndroidPlatformWindow.Current?.Input;
			if (input == null || string.IsNullOrEmpty(s))
				return;
			input.EnqueueText(s);
		}

		static void EnqueueBackspace()
		{
			var input = AndroidPlatformWindow.Current?.Input;
			if (input == null)
				return;
			input.EnqueueSpecialKey(OpenRA.Keycode.BACKSPACE, OpenRA.KeyInputEvent.Down);
			input.EnqueueSpecialKey(OpenRA.Keycode.BACKSPACE, OpenRA.KeyInputEvent.Up);
		}

		static void EnqueueSpecial(AKeycode androidKey)
		{
			var input = AndroidPlatformWindow.Current?.Input;
			if (input == null)
				return;
			var key = androidKey switch
			{
				AKeycode.Enter or AKeycode.NumpadEnter => OpenRA.Keycode.RETURN,
				AKeycode.Escape => OpenRA.Keycode.ESCAPE,
				AKeycode.Del => OpenRA.Keycode.BACKSPACE,
				_ => (OpenRA.Keycode)0
			};
			if (key == (OpenRA.Keycode)0)
				return;
			input.EnqueueSpecialKey(key, OpenRA.KeyInputEvent.Down);
			input.EnqueueSpecialKey(key, OpenRA.KeyInputEvent.Up);
		}

		/// <summary>Called from game thread / PumpInput when focus changes.</summary>
		public static void SetWanted(bool want)
		{
			if (wanted == want)
				return;
			wanted = want;
			var act = activity ?? MainActivity.Current;
			if (act == null)
				return;
			act.RunOnUiThread(() =>
			{
				if (want)
					Show();
				else
					Hide();
			});
		}

		static void Show()
		{
			if (activity == null || hiddenInput == null)
				return;
			try
			{
				suppressChange = true;
				hiddenInput.Text = "";
				lastText = "";
				suppressChange = false;
				hiddenInput.Visibility = AViewStates.Visible;
				hiddenInput.RequestFocus();
				var imm = (InputMethodManager)activity.GetSystemService(Context.InputMethodService);
				imm?.ShowSoftInput(hiddenInput, ShowFlags.Forced);
				visible = true;
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Keyboard", "Show: " + e.Message);
			}
		}

		static void Hide()
		{
			if (activity == null || hiddenInput == null)
				return;
			try
			{
				var imm = (InputMethodManager)activity.GetSystemService(Context.InputMethodService);
				imm?.HideSoftInputFromWindow(hiddenInput.WindowToken, HideSoftInputFlags.None);
				hiddenInput.ClearFocus();
				hiddenInput.Visibility = AViewStates.Invisible;
				visible = false;
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Keyboard", "Hide: " + e.Message);
			}
		}

		/// <summary>True if OpenRA widget is a text entry field.</summary>
		public static bool IsTextEntryWidget(object widget)
		{
			if (widget == null)
				return false;
			var name = widget.GetType().Name;
			return name.Contains("TextField", StringComparison.Ordinal)
			       || name.Contains("TextInput", StringComparison.Ordinal)
			       || name.Contains("PasswordField", StringComparison.Ordinal);
		}
	}
}
