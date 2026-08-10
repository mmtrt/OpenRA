// System IME bridge for OpenRA TextFieldWidget.
// Floating mode is a Gboard setting (toolbar → Floating) — apps cannot force it.
// Inside namespace OpenRA.Android always use global::Android.* for Widget/Text types.

using System;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using OpenRA.Platforms.Android;
using AKeycode = global::Android.Views.Keycode;
using AView = global::Android.Views.View;
using AViewStates = global::Android.Views.ViewStates;
using AEditText = global::Android.Widget.EditText;
using AFrameLayout = global::Android.Widget.FrameLayout;
using AInputTypes = global::Android.Text.InputTypes;
using ATextChangedEventArgs = global::Android.Text.TextChangedEventArgs;
using AImeAction = global::Android.Views.InputMethods.ImeAction;
using AImeFlags = global::Android.Views.InputMethods.ImeFlags;

namespace OpenRA.Android
{
	public static class AndroidSoftKeyboard
	{
		static Activity activity;
		static AView gameSurface;
		static AEditText hiddenInput;
		static bool wanted;
		static bool visible;
		static string lastText = "";
		static bool suppressChange;
		static long lastUserTouchMs;
		const int UserTouchGraceMs = 1200;

		public static void Attach(Activity act, AFrameLayout root, AView surface = null)
		{
			activity = act;
			if (surface != null)
				gameSurface = surface;

			try
			{
				// AdjustPan keeps the focused field above the IME without resizing GL badly
				act.Window?.SetSoftInputMode(SoftInput.AdjustPan | SoftInput.StateAlwaysHidden);
			}
			catch { /* ignore */ }

			if (hiddenInput != null)
				return;

			hiddenInput = new AEditText(act)
			{
				Focusable = true,
				FocusableInTouchMode = true,
				Visibility = AViewStates.Invisible,
				ImeOptions = (AImeAction)((int)AImeAction.Done | (int)AImeFlags.NoExtractUi | (int)AImeFlags.NoFullscreen),
				InputType = AInputTypes.ClassText
					| AInputTypes.TextFlagNoSuggestions
					| AInputTypes.TextVariationVisiblePassword
			};
			var lp = new AFrameLayout.LayoutParams(1, 1)
			{
				LeftMargin = -2000,
				TopMargin = -2000
			};
			root.AddView(hiddenInput, lp);

			hiddenInput.TextChanged += OnTextChanged;
			hiddenInput.EditorAction += (s, e) =>
			{
				if (e.ActionId == AImeAction.Done || e.ActionId == AImeAction.Go
				    || e.ActionId == AImeAction.Send || e.ActionId == AImeAction.Next)
				{
					EnqueueSpecial(AKeycode.Enter);
					ForceHide();
					e.Handled = true;
				}
			};
			hiddenInput.KeyPress += (s, e) =>
			{
				if (e.Event == null)
					return;
				if (e.Event.Action != KeyEventActions.Down)
					return;

				if (e.Event.KeyCode == AKeycode.Del)
				{
					EnqueueBackspace();
					e.Handled = true;
					return;
				}
				if (e.Event.KeyCode == AKeycode.Enter || e.Event.KeyCode == AKeycode.NumpadEnter)
				{
					EnqueueSpecial(AKeycode.Enter);
					ForceHide();
					e.Handled = true;
					return;
				}

				// Hardware / some IMEs deliver printable keys here instead of TextChanged
				// Xamarin binding requires metaState overload (no zero-arg GetUnicodeChar).
				var uni = e.Event.GetUnicodeChar((int)e.Event.MetaState);
				if (uni != 0 && !char.IsControl((char)uni))
				{
					EnqueueChars(((char)uni).ToString());
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
					// Append path (normal typing)
					EnqueueChars(text.Substring(lastText.Length));
				}
				else if (text.Length < lastText.Length)
				{
					// Deletion path
					for (var i = 0; i < lastText.Length - text.Length; i++)
						EnqueueBackspace();
				}
				else if (text != lastText && text.Length > 0)
				{
					// Same length replace (composition / autocorrect): delete all then retype
					for (var i = 0; i < lastText.Length; i++)
						EnqueueBackspace();
					EnqueueChars(text);
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
			{
				AndroidFileLog.Warn("OpenRA.Keyboard", "EnqueueChars skipped input=" + (input != null) + " s=" + (s ?? "null"));
				return;
			}
			input.EnqueueText(s);
			AndroidFileLog.Info("OpenRA.Keyboard", "typed len=" + s.Length);
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

		public static void NotifyUserTouch()
		{
			lastUserTouchMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			// Re-show IME after user dismissed it while TextField still has focus
			if (wanted)
			{
				var act = activity ?? MainActivity.Current;
				act?.RunOnUiThread(() =>
				{
					if (wanted)
						Show(force: true);
				});
			}
		}

		public static void SetWanted(bool want)
		{
			if (want && !wanted)
			{
				var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				if (now - lastUserTouchMs > UserTouchGraceMs)
					return;
			}

			if (wanted == want)
			{
				// Focus still true but IME was dismissed → allow re-show on next touch via NotifyUserTouch
				return;
			}
			wanted = want;
			var act = activity ?? MainActivity.Current;
			if (act == null)
				return;
			act.RunOnUiThread(() =>
			{
				if (want)
					Show(force: false);
				else
					Hide();
			});
		}

		public static void ForceHide()
		{
			wanted = false;
			var act = activity ?? MainActivity.Current;
			if (act == null)
			{
				Hide();
				return;
			}
			act.RunOnUiThread(Hide);
		}

		static void Show(bool force)
		{
			if (activity == null || hiddenInput == null)
				return;
			if (!wanted && !force)
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
				imm?.ShowSoftInput(hiddenInput, ShowFlags.Implicit);
				visible = true;
				AndroidFileLog.Info("OpenRA.Keyboard", "IME show force=" + force);
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
				try
				{
					var decor = activity.Window?.DecorView;
					if (decor != null)
						imm?.HideSoftInputFromWindow(decor.WindowToken, HideSoftInputFlags.None);
				}
				catch { /* ignore */ }

				hiddenInput.ClearFocus();
				hiddenInput.Visibility = AViewStates.Invisible;
				visible = false;
				try { gameSurface?.RequestFocus(); } catch { /* ignore */ }
				AndroidFileLog.Info("OpenRA.Keyboard", "IME hide");
			}
			catch (Exception e)
			{
				AndroidFileLog.Warn("OpenRA.Keyboard", "Hide: " + e.Message);
			}
		}

		public static bool IsTextEntryWidget(object widget)
		{
			if (widget == null)
				return false;
			var name = widget.GetType().Name;
			return name == "TextFieldWidget"
			       || name == "PasswordFieldWidget"
			       || name == "TextInputWidget"
			       || name.EndsWith("TextFieldWidget", StringComparison.Ordinal)
			       || name.EndsWith("PasswordFieldWidget", StringComparison.Ordinal);
		}
	}
}
