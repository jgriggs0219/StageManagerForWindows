using Microsoft.Win32;
using StageManager.Native.PInvoke;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Services
{
	internal class Desktop
	{
		[DllImport("user32.dll", SetLastError = true)]
		static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

		[DllImport("user32.dll", SetLastError = false)]
		static extern IntPtr GetDesktopWindow();

		private const int WM_COMMAND = 0x111;

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool PostMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool IsWindowVisible(IntPtr hWnd);

		private static readonly IntPtr ToggleDesktopCommand = new IntPtr(0x7402);

		// The toggle is posted, not sent, so Explorer applies it a moment later. Until it
		// does, the icon view still reports the old state — remember what was asked for so
		// a quick Hide→Show→Hide doesn't read stale state and toggle the wrong way.
		private readonly object _lock = new();
		private bool? _expected;
		private DateTime _expectedAt;
		private static readonly TimeSpan ExpectedTimeout = TimeSpan.FromSeconds(1.5);

		/// <summary>
		/// Reads the live state of the desktop icon list view. The registry value
		/// Explorer\Advanced\HideIcons is only written lazily by Explorer, so reading it
		/// right after a toggle returned the old state and the next toggle went the wrong
		/// way — which is how icons ended up never coming back.
		/// </summary>
		public bool GetDesktopIconsVisible()
		{
			var shellView = GetDesktopSHELLDLL_DefView();
			var listView = shellView != IntPtr.Zero ? FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
			if (listView != IntPtr.Zero)
				return IsWindowVisible(listView);

			using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
			if (key?.GetValue("HideIcons", 0) is int hideIconsValue)
				return hideIconsValue == 0;

			return true;
		}

		private void SetIconsVisible(bool visible, string caller)
		{
			lock (_lock)
			{
				var actual = GetDesktopIconsVisible();
				if (_expected.HasValue && (actual == _expected.Value || DateTime.UtcNow - _expectedAt > ExpectedTimeout))
					_expected = null;

				if ((_expected ?? actual) == visible)
					return;

				var shellView = GetDesktopSHELLDLL_DefView();
				if (shellView == IntPtr.Zero)
				{
					Log.Info("DESKTOP", $"{caller}: SHELLDLL_DefView not found, toggle skipped");
					return;
				}

				// Posted, not sent: SendMessage blocked the UI thread while Explorer re-laid-out
				// the whole desktop, which landed mid scene-transition as a visible hitch.
				if (PostMessage(shellView, WM_COMMAND, ToggleDesktopCommand, IntPtr.Zero))
				{
					_expected = visible;
					_expectedAt = DateTime.UtcNow;
					Log.Info("DESKTOP", $"{caller}: toggled {(visible ? "on" : "off")}");
				}
			}
		}

		/// <summary>
		/// Ensures desktop icons are toggled ON (shell-level).
		/// Call at startup for crash recovery.
		/// </summary>
		public void EnsureIconsExist() => SetIconsVisible(true, "EnsureIconsExist");

		public void ShowIcons() => SetIconsVisible(true, "ShowIcons");

		public void HideIcons(bool animate = true) => SetIconsVisible(false, "HideIcons");

		/// <summary>
		/// Restore icons visibility. Call on app shutdown. Sent synchronously here —
		/// a posted toggle can be dropped if the process exits before Explorer handles it.
		/// </summary>
		public void RestoreIcons()
		{
			if (GetDesktopIconsVisible()) return;
			var shellView = GetDesktopSHELLDLL_DefView();
			if (shellView == IntPtr.Zero) return;
			SendMessage(shellView, WM_COMMAND, ToggleDesktopCommand, IntPtr.Zero);
			Log.Info("DESKTOP", "RestoreIcons: toggled on");
		}

		static IntPtr GetDesktopSHELLDLL_DefView()
		{
			var hShellViewWin = IntPtr.Zero;
			var hWorkerW = IntPtr.Zero;

			var hProgman = FindWindow("Progman", "Program Manager");
			var hDesktopWnd = GetDesktopWindow();

			if (hProgman != IntPtr.Zero)
			{
				hShellViewWin = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);

				if (hShellViewWin == IntPtr.Zero)
				{
					// Fallback: when Progman doesn't host DefView (e.g. wallpaper rotation, toggledesktop),
					// scan WorkerW windows instead
					do
					{
						hWorkerW = FindWindowEx(hDesktopWnd, hWorkerW, "WorkerW", null);
						hShellViewWin = FindWindowEx(hWorkerW, IntPtr.Zero, "SHELLDLL_DefView", null);
					} while (hShellViewWin == IntPtr.Zero && hWorkerW != IntPtr.Zero);
				}
			}
			return hShellViewWin;
		}
	}
}
