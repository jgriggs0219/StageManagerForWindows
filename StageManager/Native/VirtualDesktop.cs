using System;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// The one virtual-desktop question Stage Manager still asks: is a window shown on all
	/// desktops (no desktop assignment)? Floating always-on-top popups such as Dialpad's
	/// notification answer yes, and are kept out of scenes. Uses the documented
	/// IVirtualDesktopManager shell interface.
	/// </summary>
	internal static class VirtualDesktop
	{
		[ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IVirtualDesktopManager
		{
			[PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
			[PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
			[PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
		}

		[ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
		private class VirtualDesktopManagerClass { }

		private static IVirtualDesktopManager? _manager;
		private static bool _unavailable;

		private static IVirtualDesktopManager? Manager
		{
			get
			{
				if (_manager is null && !_unavailable)
				{
					try { _manager = (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
					catch (Exception ex)
					{
						_unavailable = true;
						Log.Info("VDESK", $"IVirtualDesktopManager unavailable: {ex.Message}");
					}
				}
				return _manager;
			}
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

		/// <summary>
		/// True when the shell has cloaked the window — what switching virtual desktops does to
		/// every window on the desktop being left. Pure DWM query, safe in a WinEvent callback.
		/// </summary>
		public static bool IsShellCloaked(IntPtr hwnd) =>
			DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var v, sizeof(int)) == 0 && (v & 0x2 /* DWM_CLOAKED_SHELL */) != 0;

		private const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
		private static readonly System.Collections.Generic.Dictionary<IntPtr, Guid> _windowDesktop = new();

		/// <summary>The desktop the user is looking at, from Explorer's registry state (instant, callback-safe).</summary>
		public static Guid CurrentDesktopId
		{
			get
			{
				try
				{
					using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey);
					if (key?.GetValue("CurrentVirtualDesktop") is byte[] b && b.Length == 16)
						return new Guid(b);
				}
				catch { }
				return Guid.Empty;
			}
		}

		/// <summary>
		/// The desktop a window lives on, cached. The shell can't answer inside a WinEvent
		/// callback; then the current desktop is assumed (without caching), which is right for
		/// any window that just appeared.
		/// </summary>
		public static Guid DesktopOf(IntPtr hwnd)
		{
			lock (_windowDesktop)
				if (_windowDesktop.TryGetValue(hwnd, out var cached))
					return cached;
			var m = Manager;
			if (m is not null && hwnd != IntPtr.Zero)
			{
				try
				{
					if (m.GetWindowDesktopId(hwnd, out var id) == 0 && id != Guid.Empty)
					{
						lock (_windowDesktop) _windowDesktop[hwnd] = id;
						return id;
					}
				}
				catch { _manager = null; }
			}
			return CurrentDesktopId;
		}

		public static bool IsOnCurrentDesktop(IntPtr hwnd) => DesktopOf(hwnd) == CurrentDesktopId;

		public static void Forget(IntPtr hwnd)
		{
			lock (_windowDesktop) _windowDesktop.Remove(hwnd);
		}

		/// <summary>Re-reads where windows live (the user can move them in Task View). Dispatcher only.</summary>
		public static void Refresh(System.Collections.Generic.IEnumerable<IntPtr> handles)
		{
			var m = Manager;
			if (m is null) return;
			foreach (var h in handles)
			{
				try
				{
					if (m.GetWindowDesktopId(h, out var id) == 0 && id != Guid.Empty)
						lock (_windowDesktop) _windowDesktop[h] = id;
				}
				catch { _manager = null; return; }
			}
		}

		/// <summary>True when the shell reports the window as belonging to no desktop (shown on all).</summary>
		public static bool IsUnassigned(IntPtr hwnd)
		{
			var m = Manager;
			if (m is null || hwnd == IntPtr.Zero) return false;
			try { return m.GetWindowDesktopId(hwnd, out var id) == 0 && id == Guid.Empty; }
			catch (Exception) { _manager = null; return false; }
		}
	}
}
