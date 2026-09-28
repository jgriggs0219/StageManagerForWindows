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
