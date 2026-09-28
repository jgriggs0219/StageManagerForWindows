using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// Virtual desktop awareness. Stage Manager only ever manages the windows of the virtual
	/// desktop the user is looking at: windows on other desktops are left where they are, and
	/// a desktop switch (which cloaks every window on the old desktop and uncloaks the new
	/// one) must not be read as user activity.
	/// <para>
	/// The current desktop and the desktop list come from Explorer's registry state, which
	/// is updated the instant a switch happens and is cheap to read from anywhere. Which
	/// desktop a window lives on comes from the documented IVirtualDesktopManager COM
	/// interface — but that call fails inside WinEvent callbacks (input-synchronous), so
	/// answers are cached per window and only ever refreshed from the dispatcher.
	/// </para>
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

		[DllImport("dwmapi.dll")]
		private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
		private const int DWMWA_CLOAKED = 14;
		private const int DWM_CLOAKED_SHELL = 0x2;

		private const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

		private static IVirtualDesktopManager? _manager;
		private static bool _unavailable;

		// hwnd → desktop it lives on. Guid.Empty = shown on all desktops.
		private static readonly Dictionary<IntPtr, Guid> _windowDesktop = new();
		private static readonly object _lock = new();

		public record DesktopInfo(Guid Id, int Number, string Name);

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

		/// <summary>The desktop the user is looking at, straight from Explorer's registry state.</summary>
		public static Guid CurrentDesktopId
		{
			get
			{
				try
				{
					using var key = Registry.CurrentUser.OpenSubKey(RegKey);
					if (key?.GetValue("CurrentVirtualDesktop") is byte[] b && b.Length == 16)
						return new Guid(b);
				}
				catch { }
				return Guid.Empty;
			}
		}

		/// <summary>All virtual desktops in Task View order, with the user's names.</summary>
		public static IReadOnlyList<DesktopInfo> GetDesktops()
		{
			var list = new List<DesktopInfo>();
			try
			{
				using var key = Registry.CurrentUser.OpenSubKey(RegKey);
				if (key?.GetValue("VirtualDesktopIDs") is byte[] ids)
				{
					for (int i = 0; i + 16 <= ids.Length; i += 16)
					{
						var id = new Guid(ids.AsSpan(i, 16));
						var number = i / 16 + 1;
						string? name = null;
						using (var d = key.OpenSubKey($@"Desktops\{{{id}}}"))
							name = d?.GetValue("Name") as string;
						list.Add(new DesktopInfo(id, number, string.IsNullOrWhiteSpace(name) ? $"Desktop {number}" : name!));
					}
				}
			}
			catch { }
			return list;
		}

		/// <summary>
		/// The desktop a window lives on. Uses the cache; on a miss asks the shell, and if the
		/// shell can't answer (inside a WinEvent callback) assumes the current desktop without
		/// caching — true for any window that just appeared, since Windows opens new windows
		/// on, and only uncloaks windows of, the desktop being shown.
		/// </summary>
		public static Guid DesktopOf(IntPtr hwnd)
		{
			lock (_lock)
				if (_windowDesktop.TryGetValue(hwnd, out var cached))
					return cached;

			if (TryQuery(hwnd, out var id))
			{
				lock (_lock) _windowDesktop[hwnd] = id;
				return id;
			}
			return CurrentDesktopId;
		}

		/// <summary>True when the window lives on the current desktop (or on all of them).</summary>
		public static bool IsOnCurrentDesktop(IntPtr hwnd)
		{
			var d = DesktopOf(hwnd);
			return d == Guid.Empty || d == CurrentDesktopId;
		}

		/// <summary>True when the shell reports the window as belonging to no desktop (shown on all).</summary>
		public static bool IsUnassigned(IntPtr hwnd) => TryQuery(hwnd, out var id) && id == Guid.Empty;

		/// <summary>
		/// Re-asks the shell where each window lives (the user can move windows between
		/// desktops from Task View). Dispatcher only. Returns true if anything moved.
		/// </summary>
		public static bool Refresh(IEnumerable<IntPtr> handles)
		{
			var moved = false;
			foreach (var h in handles)
			{
				if (!TryQuery(h, out var id)) continue;
				lock (_lock)
				{
					if (_windowDesktop.TryGetValue(h, out var old) && old != id)
					{
						moved = true;
						Log.Info("VDESK", $"Window 0x{h.ToInt64():X} moved desktop {Short(old)} → {Short(id)}");
					}
					_windowDesktop[h] = id;
				}
			}
			return moved;
		}

		public static void Forget(IntPtr hwnd)
		{
			lock (_lock) _windowDesktop.Remove(hwnd);
		}

		/// <summary>
		/// True when the shell has cloaked the window — what a desktop switch does to every
		/// window on the desktop being left. Pure DWM query, safe inside a WinEvent callback.
		/// </summary>
		public static bool IsShellCloaked(IntPtr hwnd) =>
			DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var v, sizeof(int)) == 0 && (v & DWM_CLOAKED_SHELL) != 0;

		public static string Short(Guid id) => id == Guid.Empty ? "(all)" : id.ToString()[..8];

		private static bool TryQuery(IntPtr hwnd, out Guid id)
		{
			id = Guid.Empty;
			var m = Manager;
			if (m is null || hwnd == IntPtr.Zero) return false;
			try
			{
				return m.GetWindowDesktopId(hwnd, out id) == 0;
			}
			catch (Exception)
			{
				// Explorer restarted — the RPC proxy is dead. Recreate on next call.
				_manager = null;
				return false;
			}
		}
	}
}
