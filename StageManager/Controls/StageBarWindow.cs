using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using StageManager.Native.PInvoke;

namespace StageManager.Controls
{
	/// <summary>
	/// Small bar pinned to the top-left of the screen, inside the sidebar strip:
	///   row 1 — the group on stage, large, in its colour;
	///   row 2 — 💾 Save (remember where the app(s) on stage sit now) and
	///           📐 Use everywhere (make this size/spot the default for all single-app tiles).
	/// Topmost and never activated, so clicking it doesn't steal focus from the app on stage.
	/// </summary>
	internal sealed class StageBarWindow : Window
	{
		private readonly Emoji.Wpf.TextBlock _groupText;
		private readonly Border _pill;
		private readonly TextBlock _status;
		private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x9B, 0xFF));

		public Func<int>? OnSave { get; set; }
		public Func<bool>? OnUseEverywhere { get; set; }

		public StageBarWindow()
		{
			WindowStyle = WindowStyle.None;
			AllowsTransparency = true;
			Background = Brushes.Transparent;
			ShowInTaskbar = false;
			ShowActivated = false;
			Topmost = true;
			ResizeMode = ResizeMode.NoResize;
			SizeToContent = SizeToContent.WidthAndHeight;

			_groupText = new Emoji.Wpf.TextBlock
			{
				FontSize = 17,
				FontWeight = FontWeights.Bold,
				Foreground = Brushes.White,
				TextTrimming = TextTrimming.CharacterEllipsis,
				MaxWidth = 150,
			};
			_pill = new Border
			{
				CornerRadius = new CornerRadius(12),
				Padding = new Thickness(12, 5, 14, 5),
				BorderThickness = new Thickness(2),
				BorderBrush = _accent,
				Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1B, 0x26)),
				Child = _groupText,
				HorizontalAlignment = HorizontalAlignment.Left,
				Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black },
			};

			_status = new TextBlock { Foreground = Brushes.Gainsboro, FontSize = 11, Margin = new Thickness(4, 4, 0, 0) };

			var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
			buttons.Children.Add(MakeButton("💾", "Save", "Save where the app on stage sits right now", () =>
			{
				var n = OnSave?.Invoke() ?? 0;
				Flash(n > 0 ? "Saved ✓" : "Nothing to save");
			}));
			buttons.Children.Add(MakeButton("📐", "Everywhere", "Use this size and spot for every single-app tile (combined tiles keep theirs)", () =>
			{
				var ok = OnUseEverywhere?.Invoke() ?? false;
				Flash(ok ? "Used everywhere ✓" : "Put one app on stage first");
			}));

			var root = new StackPanel { Margin = new Thickness(14, 10, 0, 0) };
			root.Children.Add(_pill);
			root.Children.Add(buttons);
			root.Children.Add(_status);
			Content = root;

			SourceInitialized += (_, _) =>
			{
				var hwnd = new WindowInteropHelper(this).Handle;
				var ex = Win32.GetWindowExStyleLongPtr(hwnd);
				Win32.SetWindowStyleExLongPtr(hwnd, ex | Win32.WS_EX.WS_EX_TOOLWINDOW | Win32.WS_EX.WS_EX_NOACTIVATE);
			};
		}

		private Border MakeButton(string icon, string label, string tip, Action click)
		{
			var content = new StackPanel { Orientation = Orientation.Horizontal };
			content.Children.Add(new Emoji.Wpf.TextBlock { Text = icon, FontSize = 13, Margin = new Thickness(0, 0, 5, 0) });
			content.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });

			var normal = new SolidColorBrush(Color.FromArgb(0xE6, 0x24, 0x2A, 0x36));
			var hover = new SolidColorBrush(Color.FromArgb(0xF2, 0x34, 0x3D, 0x4E));
			var b = new Border
			{
				CornerRadius = new CornerRadius(9),
				Padding = new Thickness(9, 4, 10, 4),
				Margin = new Thickness(0, 0, 6, 0),
				Background = normal,
				BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
				BorderThickness = new Thickness(1),
				Cursor = Cursors.Hand,
				ToolTip = tip,
				Child = content,
			};
			b.MouseEnter += (_, _) => b.Background = hover;
			b.MouseLeave += (_, _) => b.Background = normal;
			b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
			return b;
		}

		private async void Flash(string text)
		{
			_status.Text = text;
			await Task.Delay(2200);
			if (_status.Text == text) _status.Text = "";
		}

		/// <summary>Shows the group on stage (or the app name when ungrouped) in its colour.</summary>
		public void SetGroup(string name, Color? color, Rect workArea)
		{
			_groupText.Text = name;
			var c = color is Color k && k.A > 0 ? k : Color.FromRgb(0x88, 0x88, 0x88);
			_accent.Color = c;
			_pill.BorderBrush = _accent;
			if (!workArea.IsEmpty) { Left = workArea.Left; Top = workArea.Top; }
			if (!IsVisible) Show();
		}
	}
}
