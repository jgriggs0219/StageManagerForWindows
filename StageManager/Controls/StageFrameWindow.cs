using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using StageManager.Native.PInvoke;

namespace StageManager.Controls
{
	/// <summary>
	/// A thin, glowing border around the whole work area in the colour of the group that is on
	/// stage, so it's obvious at a glance which group you're in. Topmost, click-through, never
	/// activated and not in Alt+Tab — it only draws. Fades between groups; hidden when the app on
	/// stage has no group.
	/// </summary>
	internal sealed class StageFrameWindow : Window
	{
		private readonly Border _frame;
		private readonly SolidColorBrush _brush = new(Colors.Transparent);
		private readonly DropShadowEffect _glow;
		private Color? _shown;

		public StageFrameWindow()
		{
			WindowStyle = WindowStyle.None;
			AllowsTransparency = true;
			Background = Brushes.Transparent;
			ShowInTaskbar = false;
			ShowActivated = false;
			Topmost = true;
			Focusable = false;
			IsHitTestVisible = false;
			ResizeMode = ResizeMode.NoResize;

			_glow = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.85, Color = Colors.Transparent };
			_frame = new Border
			{
				BorderThickness = new Thickness(3),
				CornerRadius = new CornerRadius(10),
				BorderBrush = _brush,
				Margin = new Thickness(2),
				Effect = _glow,
				Opacity = 0,
			};
			Content = _frame;

			SourceInitialized += (_, _) =>
			{
				// Click-through + no activation + no Alt+Tab entry.
				var hwnd = new WindowInteropHelper(this).Handle;
				var ex = Win32.GetWindowExStyleLongPtr(hwnd);
				Win32.SetWindowStyleExLongPtr(hwnd, ex | Win32.WS_EX.WS_EX_TRANSPARENT | Win32.WS_EX.WS_EX_TOOLWINDOW | Win32.WS_EX.WS_EX_NOACTIVATE | Win32.WS_EX.WS_EX_LAYERED);
			};
		}

		/// <summary>Fits the frame to the work area (DIPs).</summary>
		public void Fit(Rect workArea)
		{
			if (workArea.IsEmpty) return;
			Left = workArea.Left; Top = workArea.Top;
			Width = workArea.Width; Height = workArea.Height;
		}

		/// <summary>Shows the frame in <paramref name="color"/>, or fades it out for null.</summary>
		public void SetGroupColor(Color? color)
		{
			if (color == _shown) return;
			_shown = color;

			if (color is Color c && c.A > 0)
			{
				if (!IsVisible) Show();
				var to = new ColorAnimation(c, TimeSpan.FromMilliseconds(220));
				_brush.BeginAnimation(SolidColorBrush.ColorProperty, to);
				_glow.Color = c;
				_frame.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
			}
			else
			{
				_frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
			}
		}
	}
}
