using System;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// Thin wrapper over the documented IVirtualDesktopManager shell interface. Stage Manager
	/// only ever manages the windows of the virtual desktop the user is looking at: windows on
	/// other desktops are left exactly where they are, and a desktop switch (which cloaks every
	/// window on the old desktop and uncloaks the new one) must not be read as user activity.
	/// Must be used from the UI (STA) thread.
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
		private const int DWMWA_CLOAKED = 14;
		private const int DWM_CLOAKED_SHELL = 0x2;

		/// <summary>
		/// True when the shell has cloaked the window — which is what a virtual desktop switch
		/// does to every window on the desktop being left. Pure DWM query, no COM, so it is
		/// safe inside a WinEvent callback.
		/// </summary>
		public static bool IsShellCloaked(IntPtr hwnd) =>
			DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var v, sizeof(int)) == 0 && (v & DWM_CLOAKED_SHELL) != 0;

		/// <summary>
		/// True when the shell reports the window as belonging to no virtual desktop — shown
		/// on all of them (notification toasts, floating status pills).
		/// </summary>
		public static bool IsUnassigned(IntPtr hwnd)
		{
			var m = Manager;
			if (m is null || hwnd == IntPtr.Zero) return false;
			try { return m.GetWindowDesktopId(hwnd, out var id) == 0 && id == Guid.Empty; }
			catch (Exception) { _manager = null; return false; }
		}

		/// <summary>
		/// True when the window lives on the current virtual desktop. Fails open (true) when
		/// the shell can't answer — windows with no desktop assignment, a restarting Explorer —
		/// so behaviour degrades to the old single-desktop logic rather than ignoring windows.
		/// </summary>
		public static bool IsOnCurrentDesktop(IntPtr hwnd)
		{
			var m = Manager;
			if (m is null || hwnd == IntPtr.Zero) return true;
			try
			{
				return m.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) != 0 || on;
			}
			catch (Exception)
			{
				// Explorer restarted — the RPC proxy is dead. Recreate on next call.
				_manager = null;
				return true;
			}
		}
	}
}
