using AsyncAwaitBestPractices;
using StageManager.Helpers;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;

namespace StageManager
{
	public class SceneManager : IDisposable
	{
		private readonly Desktop _desktop;
		private List<Scene> _scenes = new List<Scene>();
		private readonly object _scenesLock = new object();
		private Scene? _current;
		private bool _suspend = false;
		private Scene? _lastScene; // remembers the scene that was active before desktop view
		private IWindow? _lastFocusedWindow;
		private DateTime _lastFocusChange = DateTime.MinValue; // Track rapid focus changes

		// Windows the user minimized themselves. A minimized window is never parked —
		// OpacityWindowStrategy.Hide skips iconic windows — so nothing else records that it
		// left the stage on purpose, and the restore passes in SwitchTo and
		// RestoreMinimizedInvisibly would drag it back on the next scene switch.
		private readonly HashSet<IntPtr> _userMinimized = new HashSet<IntPtr>();

		// When a window last left the stage by minimize/close. Windows then hands the
		// foreground to the next window in z-order, which may be a parked one — that
		// activation is not the user's and must not switch scenes.
		private DateTime _lastFocusHandoffAt = DateTime.MinValue;
		private static readonly TimeSpan FocusHandoffWindow = TimeSpan.FromMilliseconds(600);

		/// <summary>
		/// When set, focus-triggered scene switches use this delegate instead of calling SwitchTo directly.
		/// MainWindow sets this to inject the transition animation.
		/// </summary>
		public Func<Scene, Task<bool>>? AnimatedSwitch { get; set; }

		public event EventHandler<SceneChangedEventArgs>? SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs>? CurrentSceneSelectionChanged;

		// Use full-transparency instead of minimising so hidden windows keep repainting and thumbnails stay live.
		private IWindowStrategy WindowStrategy { get; } = new OpacityWindowStrategy();

		public WindowsManager WindowsManager { get; }

		private const string TeamsProcessName1 = "ms-teams.exe";
		private const string TeamsProcessName2 = "teams.exe";
		private bool _disposed = false;
		private readonly bool _hideDesktopIcons;

		/// <summary>
		/// Determines whether the given window should stay visible across scenes and therefore must not
		/// participate in Stage Manager scene logic. Currently hard-codes an exception for the Microsoft
		/// Teams ‘Meeting compact’ floating pop-up.
		/// </summary>
		private bool IsPersistentWindow(IWindow window)
		{
			if (window == null)
				return false;

			// Always-on-top popups that belong to no virtual desktop (Dialpad's notification,
			// status pills) float over everything — they are not app windows to stage.
			if ((Win32.GetWindowExStyleLongPtr(window.Handle) & Win32.WS_EX.WS_EX_TOPMOST) != 0 &&
				VirtualDesktop.IsUnassigned(window.Handle))
				return true;

			// Quick process check – bail out early if it is definitely not Teams
			var exe = window.ProcessFileName ?? string.Empty;
			if (!string.Equals(exe, TeamsProcessName1, StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(exe, TeamsProcessName2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			// Identify the floating meeting pop-up through its title. The compact meeting view always contains
			// the words “Meeting” and “compact”. Adjust the checks here if Microsoft changes the wording.
			var title = window.Title ?? string.Empty;
			return title.IndexOf("Meeting", StringComparison.OrdinalIgnoreCase) >=0 &&
			 title.IndexOf("compact", StringComparison.OrdinalIgnoreCase) >=0;
		}

		public SceneManager(WindowsManager windowsManager, bool hideDesktopIcons = true)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
			_desktop = new Desktop();
			_hideDesktopIcons = hideDesktopIcons;

			// Ensure icons exist (shell-level) so the SysListView32 is available for alpha fading.
			// If previous session crashed, icons may have been toggled off — restore them.
			// Only when the feature is on — otherwise desktop icons are the user's business entirely.
			if (_hideDesktopIcons)
				_desktop.EnsureIconsExist();

			if (_hideDesktopIcons)
				_desktop.HideIcons(animate: false);

			Log.Info("STARTUP", $"SceneManager constructor: hideDesktopIcons={_hideDesktopIcons}");
		}

		public async Task Start()
		{
			// Check if we're on the UI thread by verifying we have access to the dispatcher
			// This is more reliable than checking for thread ID1
			if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			Log.Info("STARTUP", "SceneManager starting");

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.DesktopShortClick += WindowsManager_DesktopShortClick;

			await WindowsManager.Start();

			Log.Info("STARTUP", "SceneManager started, WindowsManager active");
		}

		internal void Stop()
		{
			// Unsubscribe from all WindowsManager events to prevent memory leaks
			WindowsManager.WindowCreated -= WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated -= WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed -= WindowsManager_WindowDestroyed;
			WindowsManager.DesktopShortClick -= WindowsManager_DesktopShortClick;

			// Restore opacity BEFORE WindowsManager.Stop() clears _windows.
			var exemptHandle = _lastFocusedWindow?.Handle ?? Win32.GetForegroundWindow();
			foreach (var w in WindowsManager.Windows)
			{
				WindowStrategy.Show(w);
				if (w.Handle != exemptHandle)
				{
					w.ShowMinimized();
				}
			}

			WindowsManager.Stop();

			if (_hideDesktopIcons)
				_desktop.RestoreIcons();
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			if (_suspend)
			{
				Log.Window("EVENT", $"SUSPENDED, ignoring {type}", window);
				return;
			}

			// Virtual desktops: during a desktop switch, and for windows that live on another
			// desktop, focus/show events are the OS shuffling windows, not the user picking a tile.
			if ((type == WindowUpdateType.Foreground || type == WindowUpdateType.Show) &&
				(DateTime.UtcNow < _desktopSettleUntil || !VirtualDesktop.IsOnCurrentDesktop(window.Handle)))
			{
				Log.Window("VDESK", $"{type} ignored (desktop switch / other desktop)", window);
				return;
			}

			if (type == WindowUpdateType.Foreground)
			{
				// Skip rapid focus changes to prevent scene switching loops
				if (IsRapidFocusChange())
				{
					Log.Window("FOCUS", "RAPID focus change, skipping", window);
					return;
				}

				// A parked window is off-screen by our own hand, so any activation it wins is
				// one nobody asked for: minimizing hands the foreground to the next window in
				// the z-chain, and parked windows are still in that chain (Hide keeps their
				// z-order deliberately). Following that event would yank the stage to a scene
				// the user never picked — and would immediately undo the stow below.
				// Time-based guards do not work here: WinEvents are queued behind our own
				// hook callback, so they land after any flag we could raise has dropped.
				// Outside that hand-off window, though, a parked window coming to the foreground
				// is the user asking for it — a taskbar click, Alt+Tab, a notification. Ignoring
				// those is what made apps "not open" while Stage Manager was running.
				if (OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _)
					&& DateTime.UtcNow - _lastFocusHandoffAt < FocusHandoffWindow)
				{
					Log.Window("FOCUS", "Foreground on parked window right after minimize/close, ignoring", window);
					return;
				}

				Log.Window("FOCUS", "Foreground change", window);

				_lastFocusedWindow = window; // remember for scene restore
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
			else if (type == WindowUpdateType.MoveEnd)
			{
				RememberHandPlacement(window);
			}
			else if (type == WindowUpdateType.MinimizeStart)
			{
				OnWindowMinimized(window);
			}
			else if (type == WindowUpdateType.MinimizeEnd)
			{
				OnWindowRestored(window).SafeFireAndForget();
			}
			// Some applications surface a previously hidden window with a simple ShowWindow
			// call that does NOT bring the window to the foreground. In that case the
			// window is visible but still carries WS_EX_TRANSPARENT from our hide logic
			// and is therefore not clickable. Treat a Show event as a signal that the
			// application wants to interact again and restore normal interactivity.
			else if (type == WindowUpdateType.Show)
			{
				Log.Window("EVENT", "Show", window);

				// Option2: Make Show event authoritative for current scene windows
				var scene = FindSceneForWindow(window);
				if (scene is not null && ReferenceEquals(scene, _current))
				{
					// If the window is minimized but just got shown, ensure it is restored
					if (window.IsMinimized)
					{
						Log.Window("EVENT", "Show: restoring minimized window in current scene", window);
						window.ShowNormal();
					}

					// Force clearing opacity/mouse-through regardless of skip checks
					WindowStrategy.Show(window);
				}
				else
				{
					// Restore normal interactivity for non-active scenes;
					// WindowStrategy.Show handles its own skip-checks internally.
					WindowStrategy.Show(window);
				}

				// Only switch scenes if this is actually a focus change, not just a show event
				// This prevents scene creation for minimized windows that shouldn't create scenes
				if (window.IsFocused)
				{
					Log.Window("EVENT", "Show + focused → switching scene", window);
					// Bring Stage Manager's focus model in sync by switching to the scene
					// containing this window. This guarantees proper stacking order and
					// icon visibility handling.
					SwitchToSceneByWindow(window).SafeFireAndForget();
				}
			}
		}

		/// <summary>
		/// True when the user minimized this window and has not restored it since. Such a
		/// window stays off the stage even when its scene comes back — the same way macOS
		/// leaves a minimized window in the Dock while its group is on stage.
		/// </summary>
		private bool IsUserMinimized(IWindow window)
		{
			lock (_userMinimized)
				return _userMinimized.Contains(window.Handle);
		}

		/// <summary>
		/// Drops the user-minimized mark, for callers that un-minimize a window on the user's
		/// behalf (dragging its tile out of the sidebar, moving it between scenes). Clearing it
		/// before the ShowWindow call also stops the resulting MINIMIZEEND from being read as
		/// the user restoring the window from the taskbar.
		/// </summary>
		public void ForgetUserMinimized(IWindow window)
		{
			lock (_userMinimized)
				_userMinimized.Remove(window.Handle);
		}

		/// <summary>
		/// Handles the user minimizing a window. The window itself leaves the stage for good
		/// until it is restored; when it was the last one keeping the current scene on screen,
		/// the whole scene is stowed back into the sidebar and the desktop is shown — the same
		/// end state a blank desktop click produces.
		/// </summary>
		private void OnWindowMinimized(IWindow window)
		{
			_lastFocusHandoffAt = DateTime.UtcNow;
			if (_suspend || IsPersistentWindow(window))
				return;

			lock (_userMinimized)
				_userMinimized.Add(window.Handle);

			Log.Window("MINIMIZE", "User minimized", window);

			var scene = FindSceneForWindow(window);
			if (scene is null || !ReferenceEquals(scene, _current))
				return;

			// Exclude this handle explicitly rather than trusting IsMinimized: MINIMIZESTART
			// fires at the start of the animation and the iconic flag may not be set yet.
			if (scene.Windows.Any(w => w.Handle != window.Handle && !w.IsMinimized))
				return;

			Log.Action($"Last window of '{scene.Title}' minimized → stow scene, show desktop");
			SwitchTo(null).SafeFireAndForget();
		}

		/// <summary>
		/// Handles the user restoring a minimized window from the taskbar. Switches to the
		/// window's scene explicitly instead of leaving it to the foreground event that
		/// follows: the window may still be parked off-screen from an earlier scene switch,
		/// and the parked-window guard in the foreground handler would (correctly) drop it.
		/// </summary>
		private async Task OnWindowRestored(IWindow window)
		{
			bool wasUserMinimized;
			lock (_userMinimized)
				wasUserMinimized = _userMinimized.Remove(window.Handle);

			// Our own restore passes call ShowNormal and echo a MINIMIZEEND straight back
			// here. Those windows were never marked, so the flag doubles as the echo guard.
			if (!wasUserMinimized || _suspend)
				return;

			Log.Window("MINIMIZE", "User restored from taskbar", window);

			var scene = FindSceneForWindow(window);
			if (scene is null)
				return;

			if (ReferenceEquals(scene, _current))
			{
				WindowStrategy.Show(window);
				return;
			}

			if (AnimatedSwitch != null)
				await AnimatedSwitch(scene);
			else
				await SwitchTo(scene);
		}

		private bool IsBlankDesktopClick(IntPtr handle)
		{
			var cls = DesktopShellClassifier.GetClassName(handle);

			// Ignore taskbar / other common shells
			if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase))
				return false;

			// Helper local function to evaluate selection count on a SysListView32 window
			static bool IsListViewSelectionEmpty(IntPtr listView)
			{
				if (listView == IntPtr.Zero)
					return true;

				var sel = Win32.SendMessage(listView, Win32.LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero);
				return sel == IntPtr.Zero;
			}

			// Desktop background container windows (WorkerW/Progman): blank click only when no icon selected
			if (DesktopShellClassifier.IsDesktopBackground(handle))
			{
				var shell = Desktop.FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
				var listView = shell != IntPtr.Zero ? Desktop.FindWindowEx(shell, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
				return IsListViewSelectionEmpty(listView);
			}

			// Desktop icon view (list view) – ensure no icon is selected
			if (DesktopShellClassifier.IsDesktopIconView(handle))
				return IsListViewSelectionEmpty(handle);

			return false;
		}

		// MainWindow assigns this so SceneManager can ignore desktop-toggle clicks while the
		// app-filter is active — those clicks are reserved for clearing the filter (handled in
		// MainWindow.OnMousePressed).
		public Func<bool>? IsAppFilterActive { get; set; }

		private void WindowsManager_DesktopShortClick(object? sender, IntPtr handle)
		{
			if (_suspend || !StageManager.Services.Settings.GetClickDesktopToShowDesktop())
				return;

			if (IsAppFilterActive?.Invoke() == true)
			{
				Log.Info("DESKTOP", "Click during app-filter — desktop toggle suppressed");
				return;
			}

			// Only treat clicks on truly blank desktop areas as a toggle trigger
			if (!IsBlankDesktopClick(handle))
			{
				Log.Info("DESKTOP", "Click on desktop icon, not blank area — ignoring", handle);
				return;
			}

			if (_current is null)
			{
				Log.Action($"Desktop click → restore last scene '{_lastScene?.Title}'");
				if (_lastScene is object)
					SwitchTo(_lastScene).SafeFireAndForget();
			}
			else
			{
				Log.Action($"Desktop click → show desktop (hiding '{_current?.Title}')");
				SwitchTo(null).SafeFireAndForget();
			}
		}

		private void WindowsManager_WindowDestroyed(IWindow window)
		{
			Log.Window("EVENT", "WindowDestroyed", window);
			VirtualDesktop.Forget(window.Handle);
			AppGroups.ForgetWindow(window.Handle);
			_lastFocusHandoffAt = DateTime.UtcNow;

			OpacityWindowStrategy.CleanupWindow(window.Handle);
			ForgetZOrder(window.Handle);
			ForgetUserMinimized(window);

			var scene = FindSceneForWindow(window);

			if (scene is not null)
			{
				scene.Remove(window);
				Log.Scene("Window removed from scene", scene, window);

				if (scene.Windows.Any())
				{
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));

					// If the removed window was focused, ensure another window from the same scene is shown shortly.
					if (ReferenceEquals(scene, _current))
					{
						Task.Run(async () =>
						{
							await Task.Delay(300);
							var first = scene.Windows.FirstOrDefault();
							if (first is object)
							{
								// Reveal and focus the first remaining window of the current scene
								WindowStrategy.Show(first);
								first.Focus();
							}
						});
					}
				}
				else
				{
					Log.Scene("Scene empty, removing", scene);
					lock (_scenesLock)
						_scenes.Remove(scene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));

					// If current scene became empty, switch to the first available scene after a short delay.
					if (ReferenceEquals(scene, _current))
					{
						Task.Run(async () =>
						{
							await Task.Delay(200);
							Scene? fallback;
							lock (_scenesLock)
								fallback = _scenes.FirstOrDefault(s => s.Windows.Any());
							if (fallback is not null)
								await SwitchTo(fallback).ConfigureAwait(false);
						});
					}
				}
			}
		}

		public Scene? FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene? FindSceneForWindow(IntPtr handle)
		{
			lock (_scenesLock)
				return _scenes.FirstOrDefault(s => s.Windows.Any(w => w.Handle == handle));
		}

		private Scene? FindSceneForProcess(string processName)
		{
			lock (_scenesLock)
				return _scenes.FirstOrDefault(s => string.Equals(s.Key, processName, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Pulls tracked windows of the same app that belong to no scene into
		/// <paramref name="scene"/>. A window only joins a scene when it gains focus, so an
		/// app that opens several windows at once leaves every unfocused one orphaned —
		/// parked off-screen with no tile and no way of ever coming back.
		/// </summary>
		private void AdoptOrphanWindows(Scene scene)
		{
			foreach (var window in WindowsManager.Windows.ToArray())
			{
				if (IsPersistentWindow(window) || FindSceneForWindow(window) is not null)
					continue;
				if (!string.Equals(GetWindowGroupKey(window), scene.Key, StringComparison.OrdinalIgnoreCase))
					continue;

				scene.Add(window);
				Log.Scene("Adopted orphaned window of the same app", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}
		}

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			SwitchToSceneByNewWindow(window).SafeFireAndForget();
		}

		private async Task SwitchToSceneByWindow(IWindow window)
		{
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "Persistent window, skipping scene logic", window);
				return;
			}

			// Only create/switch scenes for windows that are actually focused, not just shown
			// This prevents scene creation for minimized windows that get Show events without focus
			if (!window.IsFocused)
			{
				Log.Window("SCENE", "Window not focused, skipping scene switch", window);
				return;
			}

			var scene = FindSceneForWindow(window);
			if (scene is null)
			{
				// Window not yet bound to any scene. Before creating a new one, check whether
				// a scene already exists for this process (race: focus event can fire before
				// SwitchToSceneByNewWindow has bound the window). If so, adopt the window into
				// the existing scene to prevent duplicate scenes for the same app.
				var key = GetWindowGroupKey(window);
				var byKey = FindSceneForProcess(key);
				if (byKey is not null)
				{
					byKey.Add(window);
					scene = byKey;
					Log.Scene("Adopted window into existing scene by process key", scene, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
				}
				else
				{
					scene = new Scene(key, window);
					lock (_scenesLock)
						_scenes.Add(scene);
					Log.Scene("Created new scene for window", scene, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
					AdoptOrphanWindows(scene);
				}
			}
			else
			{
				Log.Scene("Switching to existing scene", scene, window);
			}

			if (AnimatedSwitch != null)
				await AnimatedSwitch(scene);
			else
				await SwitchTo(scene);
		}

		private async Task SwitchToSceneByNewWindow(IWindow window)
		{
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "New persistent window, skipping", window);
				return;
			}

			// Only CREATE or SWITCH scenes for windows that are actually focused, not just
			// created. An unfocused new window still has to be bound to its app's scene if
			// one exists, or it belongs nowhere and stays parked off-screen for good; it
			// just must not drag the stage over to itself.
			if (!window.IsFocused)
			{
				var ownerScene = FindSceneForProcess(GetWindowGroupKey(window));
				if (ownerScene is null || FindSceneForWindow(window) is not null)
				{
					Log.Window("SCENE", "New window not focused, skipping", window);
					return;
				}

				ownerScene.Add(window);
				Log.Scene("New unfocused window → added to its app's scene", ownerScene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(ownerScene, window, ChangeType.Updated));

				// Match the scene it just joined: on stage if that scene is showing, parked
				// with the others if not.
				if (ReferenceEquals(ownerScene, _current))
					WindowStrategy.Show(window);
				else
					WindowStrategy.Hide(window);
				return;
			}

			// Use the group key (process id) consistently to guarantee a new process -> new scene
			var key = GetWindowGroupKey(window);
			var existentScene = FindSceneForProcess(key);
			var scene = existentScene ?? new Scene(key, window);

			if (existentScene is null)
			{
				lock (_scenesLock)
					_scenes.Add(scene);
				Log.Scene("New window → new scene created", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
				AdoptOrphanWindows(scene);
			}
			else
			{
				scene.Add(window);
				Log.Scene("New window → added to existing scene", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			await SwitchTo(scene).ConfigureAwait(true);
		}

		/// <summary>
		/// Determines if a scene is switched back to shortly after it has been hidden.
		/// This can happen if an app activates one of it's windows after being hidde,
		/// like Microsoft Teams does if there's a small floating window for a current call.
		/// </summary>
		/// <param name="scene"></param>
		/// <returns></returns>
		/// <summary>
		/// Determines if focus changes are happening too rapidly to indicate system vs user interaction
		/// This helps prevent scene switching loops from automatic focus changes
		/// </summary>
		/// <returns></returns>
		private bool IsRapidFocusChange()
		{
			var now = DateTime.Now;
			if ((now - _lastFocusChange).TotalMilliseconds <100) // Less than100ms since last focus change
			{
				Log.Info("FOCUS", "Rapid focus change detected, filtering");
				_lastFocusChange = now;
				return true; // This is a rapid focus change
			}
			_lastFocusChange = now;
			return false;
		}

		public async Task<bool> SwitchTo(Scene? scene)
		{
			if (object.Equals(scene, _current))
			{
				Log.Info("SWITCH", $"Already on scene '{scene?.Title}', skipping");
				return false;
			}

			Log.Info("SWITCH", $"SwitchTo START: '{_current?.Title}' → '{scene?.Title ?? "(desktop)"}'");

			IWindow? focusCandidate = null;

			try
			{
				_suspend = true;

				// Determine the window that currently has the keyboard focus (foreground).
				var foregroundHandle = Win32.GetForegroundWindow();

				// When switching to a scene, skip the foreground window (it gets focus handling separately).
				// When switching to desktop (scene=null), hide ALL windows including the foreground.
				var otherWindows = GetSceneableWindows()
					.Except(scene?.Windows ?? Array.Empty<IWindow>())
					.Where(w => scene is null || w.Handle != foregroundHandle)
					// Only this desktop: windows on other desktops keep their own stage.
					.Where(w => VirtualDesktop.IsOnCurrentDesktop(w.Handle))
					.ToArray();

				var prior = _current;
				_current = scene;

				Scene[] scenesSnapshot;
				lock (_scenesLock)
					scenesSnapshot = _scenes.ToArray();
				foreach (var s in scenesSnapshot)
				{
					s.IsSelected = s.Equals(scene);
				}

				// Read the outgoing stacking BEFORE anything is hidden — parking a window
				// off-screen leaves it in the z-chain, but the order is only meaningful while
				// the scene is still the one on screen.
				CaptureZOrder(otherWindows);

				Log.Info("SWITCH", $"Hiding {otherWindows.Length} windows");
				foreach (var o in otherWindows)
				{
					Log.Window("HIDE", "Hiding", o);
					WindowStrategy.Hide(o);
				}

				// Size + centre (or side-by-side) before anything is shown, so windows appear in place.
				ApplyStageLayout(scene);

				// Phase2: bring in target-scene windows.
				if (scene is object)
				{
					Log.Frame("SWITCH", $"Show pass START, {scene.Windows.Count()} windows in target scene");
					// Bottom-most first: Show ends in BringWindowToTop, so whichever window is
					// shown last ends up on top. Feeding them in reverse depth order replays the
					// stacking the scene had when it was last on screen.
					foreach (var w in OrderBottomToTop(scene.Windows.ToArray().Where(w => VirtualDesktop.IsOnCurrentDesktop(w.Handle)).ToArray()))
					{
						// Option1: Restore-then-clear for any minimized window in the active scene.
						// A window the user minimized stays minimized: Show below skips iconic
						// windows, so it keeps its taskbar button and never reaches the stage.
						if (w.IsMinimized && !IsUserMinimized(w))
						{
							Log.Window("SHOW", "Restoring minimized", w);
							w.ShowNormal();
						}

						Log.Window("SHOW", "Showing", w);
						// Always clear any previous opacity/click-through for active scene windows
						WindowStrategy.Show(w);
					}
					// Same frame as START means the whole scene was unparked inside one compose
					// and any remaining stagger is downstream of here — the cards, or the apps
					// repainting. A later frame means the pass itself is what splits them.
					Log.Frame("SWITCH", "Show pass END");

					// Determine which window should get focus after restore – pick the last
					// focused window if it belongs to the scene and is not minimised, otherwise
					// the one that was frontmost. Focusing raises a window to the top, so taking
					// the first in list order here would undo the stacking just restored above.
					if (_lastFocusedWindow is object && scene.Windows.Contains(_lastFocusedWindow) && !_lastFocusedWindow.IsMinimized)
						focusCandidate = _lastFocusedWindow;
					else
						focusCandidate = OrderBottomToTop(scene.Windows.ToArray().Where(w => VirtualDesktop.IsOnCurrentDesktop(w.Handle)).ToArray()).LastOrDefault(w => !w.IsMinimized);

					Log.Window("SWITCH", "Focus candidate", focusCandidate ?? scene.Windows.FirstOrDefault());
				}

				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));

				if (scene is null)
				{
					_lastScene = prior;
					if (_hideDesktopIcons)
					{
						Log.Info("DESKTOP", "Showing desktop icons (switched to desktop view)");
						_desktop.ShowIcons();
					}
				}
				else
				{
					_lastScene = null;
					if (_hideDesktopIcons)
					{
						Log.Info("DESKTOP", "Hiding desktop icons (switched to scene)");
						_desktop.HideIcons();
					}
				}
			}
			finally
			{
				_suspend = false;

				// Apply focus once suspension lifted
				if (focusCandidate is object)
					focusCandidate.Focus();

				Log.Info("SWITCH", $"SwitchTo END: now on '{_current?.Title ?? "(desktop)"}'");
			}

			return true;
		}

		/// <summary>
		/// Re-sorts windows into scenes after group membership changed (AppGroups.Changed, a
		/// new window, a title that now matches a rule). A scene is one app within one group,
		/// so a Chrome window moved to its own group splits off into its own tile. The stage
		/// is left as the user sees it: a window on stage stays on stage.
		/// </summary>
		public void RegroupWindows()
		{
			foreach (var window in GetSceneableWindows().ToArray())
			{
				var source = FindSceneForWindow(window);
				if (source is null)
					continue;
				var key = GetWindowGroupKey(window);
				if (string.Equals(source.Key, key, StringComparison.OrdinalIgnoreCase))
					continue;

				var target = FindSceneForProcess(key);
				var created = target is null;
				target ??= new Scene(key);

				Log.Window("GROUPS", $"Regroup '{source.Key}' → '{key}'", window);
				source.Remove(window);
				target.Add(window);
				if (created)
				{
					lock (_scenesLock)
						_scenes.Add(target);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, window, ChangeType.Created));
				}
				else
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, window, ChangeType.Updated));

				var sourceEmpty = !source.Windows.Any();
				if (sourceEmpty)
				{
					lock (_scenesLock)
						_scenes.Remove(source);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, window, ChangeType.Removed));
				}
				else
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, window, ChangeType.Updated));

				if (ReferenceEquals(source, _current))
				{
					if (sourceEmpty)
					{
						// The whole stage moved groups: the new scene is what's on stage now.
						_current = target;
						target.IsSelected = true;
						CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(source, target));
					}
					else if (!ReferenceEquals(target, _current))
						WindowStrategy.Hide(window);
				}
			}
		}

		/// <summary>
		/// The stage — the work area right of the sidebar — in physical pixels. Set by MainWindow.
		/// </summary>
		public Func<System.Drawing.Rectangle>? StageArea { get; set; }

		private const int StageMargin = 24;
		private const int StageGap = 16;

		/// <summary>
		/// A name for a tile that survives restarts: the remembered split, or the app's exe +
		/// group. The scene key itself starts with the PROCESS ID, which changes every time the
		/// app starts — keying saved positions on it is why they were lost on every restart.
		/// </summary>
		public static string StableTileId(Scene scene)
		{
			var k = scene.Key;
			if (k.StartsWith("split:", StringComparison.OrdinalIgnoreCase)) return k;
			var hash = k.IndexOf('#');
			var exe = scene.Windows.FirstOrDefault()?.ProcessFileName ?? "";
			return exe + (hash >= 0 ? k.Substring(hash) : "");
		}

		private static string LayoutKey(Scene scene, IWindow w, int dupIndex) =>
			$"{StableTileId(scene)}|{w.ProcessFileName}{(dupIndex > 0 ? "#" + dupIndex : "")}";

		/// <summary>
		/// Sizes and positions a scene's windows for the stage: one app gets a standard size,
		/// centred; combined apps get equal side-by-side columns. A spot the user chose by hand
		/// (remembered on move/resize) always wins. Maximized and minimized windows are left
		/// alone. Parked windows get their return point updated, so Show lands them there.
		/// </summary>
		public void ApplyStageLayout(Scene? scene)
		{
			if (scene is null || !Settings.GetAutoArrange() || StageArea is null)
				return;

			var area = StageArea();
			if (area.Width <= 0 || area.Height <= 0)
			{
				Log.Info("LAYOUT", $"Skipped layout for '{scene.Title}': stage area unknown");
				return;
			}

			var windows = scene.Windows.ToArray()
				.Where(w => !Win32.IsIconic(w.Handle))
				.OrderBy(w => w.ProcessFileName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(w => w.Handle.ToInt64())
				.ToArray();
			if (windows.Length == 0)
				return;

			// Maximized apps (Chrome, Discord, Dialpad often reopen maximized) get the standard
			// layout too: un-maximize without activating, then place. Skipping them left them
			// full-screen over the sidebar and never snapped back after a restart.
			foreach (var w in windows.Where(w => Win32.IsZoomed(w.Handle)))
				Win32.ShowWindow(w.Handle, Win32.SW.SW_SHOWNOACTIVATE);

			int n = windows.Length;
			int innerW = area.Width - 2 * StageMargin, innerH = area.Height - 2 * StageMargin;
			int colW = (innerW - (n - 1) * StageGap) / n;

			var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < n; i++)
			{
				var w = windows[i];
				var exe = w.ProcessFileName ?? "";
				seen[exe] = seen.TryGetValue(exe, out var c) ? c + 1 : 0;

				int x, y, width, height;
				var lkey = LayoutKey(scene, w, seen[exe]);
				if (AppGroups.GetLayout(lkey) is int[] r)
				{
					(x, y, width, height) = (r[0], r[1], r[2], r[3]);
				}
				else if (n == 1 && !StableTileId(scene).StartsWith("split:", StringComparison.OrdinalIgnoreCase) && AppGroups.GetDefaultStage() is int[] d)
				{
					// "Use everywhere": the size and spot the user picked for all single-app tiles.
					(x, y, width, height) = (d[0], d[1], d[2], d[3]);
				}
				else
				{
					x = area.Left + StageMargin + i * (colW + StageGap);
					y = area.Top + StageMargin;
					width = colW;
					height = innerH;
				}

				// Keep it on the stage whatever was remembered (monitor or sidebar changes).
				width = Math.Min(width, area.Width);
				height = Math.Min(height, area.Height);
				x = Math.Clamp(x, area.Left, area.Right - width);
				y = Math.Clamp(y, area.Top, area.Bottom - height);

				Log.Window("LAYOUT", $"Place {lkey} → ({x},{y} {width}x{height})", w);
				PlaceVisibleRect(w.Handle, x, y, width, height);
			}
		}

		/// <summary>
		/// Positions a window so its VISIBLE frame is exactly the given rect. Windows 10/11 draw
		/// an invisible resize border around most windows, so SetWindowPos alone leaves gaps.
		/// </summary>
		private static readonly Dictionary<IntPtr, (int L, int T, int R, int B)> _frameInsets = new();

		private static void PlaceVisibleRect(IntPtr hwnd, int x, int y, int w, int h)
		{
			// Invisible-border insets can only be measured while the window is ON screen: for a
			// window parked past the screen edge DWM reports a clipped frame, and using that made
			// windows creep up/down a little on every switch. Measure on screen, reuse when parked.
			var parked = OpacityWindowStrategy.TryGetOriginalPosition(hwnd, out _, out _);
			if (!parked)
			{
				var outer = new Win32.Rect();
				if (Win32.GetWindowRect(hwnd, ref outer) &&
					Win32.DwmGetWindowAttribute(hwnd, (int)Win32.DwmWindowAttribute.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.Rect frame, Marshal.SizeOf<Win32.Rect>()) == 0)
				{
					var ins = (frame.Left - outer.Left, frame.Top - outer.Top, outer.Right - frame.Right, outer.Bottom - frame.Bottom);
					// Sanity: real borders are a few pixels; anything else is a bad reading.
					if (ins.Item1 is >= 0 and <= 40 && ins.Item2 is >= 0 and <= 40 && ins.Item3 is >= 0 and <= 40 && ins.Item4 is >= 0 and <= 40)
						lock (_frameInsets) _frameInsets[hwnd] = ins;
				}
			}
			(int l, int t, int rgt, int b) = (0, 0, 0, 0);
			lock (_frameInsets)
				if (_frameInsets.TryGetValue(hwnd, out var cached))
					(l, t, rgt, b) = cached;
			int ox = x - l, oy = y - t, ow = w + l + rgt, oh = h + t + b;

			// Already exactly there: don't touch it (no jitter, no needless repaint).
			var now = new Win32.Rect();
			if (!parked && Win32.GetWindowRect(hwnd, ref now) && now.Left == ox && now.Top == oy && now.Width == ow && now.Height == oh)
				return;

			if (OpacityWindowStrategy.TrySetOriginalPosition(hwnd, ox, oy))
			{
				// Parked: resize in place; Show brings it back to (ox, oy).
				Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, ow, oh,
					Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
			}
			else
			{
				Win32.SetWindowPos(hwnd, IntPtr.Zero, ox, oy, ow, oh,
					Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
			}
		}

		/// <summary>
		/// The user finished moving/resizing a window: remember that spot for its tile, so the
		/// automatic layout uses it from now on (also after restarts). Checked a moment later so
		/// drags that end in the sidebar (parking the window) aren't remembered.
		/// </summary>
		private void RememberHandPlacement(IWindow window)
		{
			var dispatcher = Application.Current?.Dispatcher;
			if (dispatcher is null) return;
			dispatcher.BeginInvoke(new Action(async () =>
			{
				await Task.Delay(400);
				var scene = FindSceneForWindow(window);
				if (scene is null || !ReferenceEquals(scene, _current)) return;
				if (OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _)) return;
				if (Win32.IsIconic(window.Handle) || Win32.IsZoomed(window.Handle)) return;
				if (Win32.DwmGetWindowAttribute(window.Handle, (int)Win32.DwmWindowAttribute.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.Rect f, Marshal.SizeOf<Win32.Rect>()) != 0) return;

				var area = StageArea?.Invoke() ?? System.Drawing.Rectangle.Empty;
				var cx = (f.Left + f.Right) / 2;
				if (!area.IsEmpty && cx < area.Left) return; // dropped over the sidebar

				var exe = window.ProcessFileName ?? "";
				var dup = scene.Windows.ToArray()
					.Where(w => string.Equals(w.ProcessFileName, exe, StringComparison.OrdinalIgnoreCase) && !Win32.IsIconic(w.Handle) && !Win32.IsZoomed(w.Handle))
					.OrderBy(w => w.Handle.ToInt64()).ToList().IndexOf(window);
				var key = LayoutKey(scene, window, Math.Max(0, dup));
				AppGroups.SaveLayout(key, f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top);
				Log.Window("LAYOUT", $"Remembered hand placement {key} = ({f.Left},{f.Top} {f.Right - f.Left}x{f.Bottom - f.Top})", window);
			}));
		}

		/// <summary>Visible frame of a window in physical px, or null if it isn't placeable now.</summary>
		private static Win32.Rect? VisibleFrame(IWindow w)
		{
			if (Win32.IsIconic(w.Handle) || Win32.IsZoomed(w.Handle)) return null;
			if (OpacityWindowStrategy.TryGetOriginalPosition(w.Handle, out _, out _)) return null;
			return Win32.DwmGetWindowAttribute(w.Handle, (int)Win32.DwmWindowAttribute.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.Rect f, Marshal.SizeOf<Win32.Rect>()) == 0 ? f : null;
		}

		/// <summary>
		/// "Save": remembers exactly where every app on stage sits right now, for this tile.
		/// Returns how many windows were saved.
		/// </summary>
		public int SaveStagePlacement()
		{
			var scene = _current;
			if (scene is null) return 0;
			int saved = 0;
			var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var w in scene.Windows.ToArray().Where(w => !Win32.IsIconic(w.Handle))
				.OrderBy(w => w.ProcessFileName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Handle.ToInt64()))
			{
				var exe = w.ProcessFileName ?? "";
				seen[exe] = seen.TryGetValue(exe, out var c) ? c + 1 : 0;
				if (VisibleFrame(w) is not Win32.Rect f) continue;
				var key = LayoutKey(scene, w, seen[exe]);
				AppGroups.SaveLayout(key, f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top);
				Log.Window("LAYOUT", $"Saved (button) {key} = ({f.Left},{f.Top} {f.Right - f.Left}x{f.Bottom - f.Top})", w);
				saved++;
			}
			return saved;
		}

		/// <summary>
		/// "Use everywhere": the on-stage app's size and spot become the default for every
		/// single-app tile in every group. Combined tiles keep their side-by-side layout.
		/// </summary>
		public bool UseStagePlacementEverywhere()
		{
			var w = _current?.Windows.ToArray().FirstOrDefault(x => !Win32.IsIconic(x.Handle));
			if (w is null || VisibleFrame(w) is not Win32.Rect f) return false;
			AppGroups.SetDefaultStage(f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top);
			Log.Window("LAYOUT", $"Use everywhere = ({f.Left},{f.Top} {f.Right - f.Left}x{f.Bottom - f.Top})", w);
			return true;
		}

		public Task MoveWindow(Scene sourceScene, IWindow window, Scene targetScene)
		{
			try
			{
				_suspend = true;

				if (sourceScene is null || sourceScene.Equals(targetScene))
					return Task.CompletedTask;

				Log.Window("MOVE", $"Moving from '{sourceScene.Title}' → '{targetScene.Title}'", window);

				sourceScene.Remove(window);
				targetScene.Add(window);

				// Remember the combination so it survives restarts (and so RegroupWindows keeps it).
				// Only apps whose windows are ALL in this tile join the combo: Chrome with windows
				// everywhere must not drag every Chrome window into it. The combo keeps this tile's group.
				var allHere = targetScene.Windows.Select(w => w.ProcessFileName ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
					.Where(exe => GetSceneableWindows().Where(w => string.Equals(w.ProcessFileName, exe, StringComparison.OrdinalIgnoreCase)).All(w => targetScene.Windows.Contains(w)))
					.ToList();
				var tileGroup = GroupOfKey(targetScene.Key);
				AppGroups.RecordSplit(allHere, tileGroup);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(targetScene, window, ChangeType.Updated));

				if (!sourceScene.Windows.Any())
				{
					Log.Scene("Source scene empty after move, removing", sourceScene);
					lock (_scenesLock)
						_scenes.Remove(sourceScene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Removed));
				}

				if (targetScene.Equals(_current))
					ApplyStageLayout(targetScene); // a new app joined the stage: re-arrange side by side

				if (targetScene.Equals(_current))
				{
					if (window.IsMinimized)
					{
						Log.Window("MOVE", "Restoring minimized window before showing", window);
						// Dragging a window onto the current scene is the user asking for it
						// back, so it stops counting as deliberately minimized.
						ForgetUserMinimized(window);
						Win32Helper.SetAlpha(window.Handle, 0);
						window.ShowNormal();
					}
					Log.Window("MOVE", "Target is current scene, showing window", window);
					WindowStrategy.Show(window);
					window.Focus();
				}
				else
				{
					Log.Window("MOVE", "Target is not current scene, hiding window", window);
					WindowStrategy.Hide(window);

					// reset window position after move so that the window is back at the starting position on the new scene
					if (window is WindowsWindow w && w.PopLastLocation() is IWindowLocation l)
						Win32.SetWindowPos(window.Handle, IntPtr.Zero, l.X, l.Y,0,0, Win32.SetWindowPosFlags.IgnoreResize);
				}

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		public async Task MoveWindow(IntPtr handle, Scene targetScene)
		{
			var source = FindSceneForWindow(handle);

			if (source is null || source.Equals(targetScene))
				return;

			var window = source.Windows.First(w => w.Handle == handle);
			await MoveWindow(source, window, targetScene);
		}

		public async Task PopWindowFrom(Scene sourceScene)
		{
			if (sourceScene is null || _current is null || sourceScene.Equals(_current))
				return;

			var window = sourceScene.Windows.LastOrDefault();

			if (window is object)
			{
				Log.Window("DRAG", $"Pulling window from '{sourceScene.Title}' into '{_current.Title}'", window);
				await MoveWindow(sourceScene, window, _current).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Removes a window from its current scene and creates a new scene for it in the sidebar.
		/// The window is hidden (alpha→0). Returns the new scene, or null if the operation was skipped.
		/// </summary>
		public Scene? SeparateWindowToNewScene(IWindow window)
		{
			var source = FindSceneForWindow(window);
			if (source == null || !source.Equals(_current))
			{
				Log.Window("DRAG", "SeparateWindow skipped: not in current scene", window);
				return null;
			}

			if (source.Windows.Count() <= 1)
			{
				Log.Window("DRAG", "SeparateWindow skipped: last window in scene", window);
				return null;
			}

			try
			{
				_suspend = true;

				Log.Window("DRAG", $"Separating from '{source.Title}' into new scene", window);

				source.Remove(window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, window, ChangeType.Updated));

				// Pulled out of a combined tile on purpose: stop remembering it as part of the split
				// (unless another window of the same app stays behind in that tile).
				if (window.ProcessFileName is string exe && !source.Windows.Any(w => string.Equals(w.ProcessFileName, exe, StringComparison.OrdinalIgnoreCase)))
					AppGroups.RemoveFromSplit(exe);

				var newScene = new Scene(GetWindowGroupKey(window), window);
				lock (_scenesLock)
					_scenes.Add(newScene);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(newScene, window, ChangeType.Created));

				WindowStrategy.Hide(window);

				return newScene;
			}
			finally
			{
				_suspend = false;
			}
		}

		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager.Windows.Where(w => !IsPersistentWindow(w) && w.CanLayout && !string.IsNullOrEmpty(w.ProcessFileName) && !string.IsNullOrEmpty(w.Title));

		public IEnumerable<Scene> GetScenes()
		{
			lock (_scenesLock)
			{
				if (_scenes.Count == 0)
				{
					var restorePath = App.RestoreScenesPath;
					if (restorePath != null)
					{
						_scenes = RestoreScenesFromSnapshot(restorePath) ?? new List<Scene>();
						UpdateService.CleanupStagingFolder();
					}

					if (_scenes.Count == 0)
					{
						_scenes = GetSceneableWindows()
							// Include all windows during initial startup (including minimized ones) for automatic scene population
							.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
							.GroupBy(GetWindowGroupKey)
							.Select(group => new Scene(group.Key, group.ToArray()))
							.ToList();
					}

					Log.Info("STARTUP", $"Initial scenes: {_scenes.Count}");
					foreach (var scene in _scenes)
						Log.Scene("Initial scene", scene);
				}

				return _scenes.ToList();
			}
		}

		public SceneSnapshot.Snapshot CreateSnapshot()
		{
			Scene[] snap;
			Scene? currentScene;
			lock (_scenesLock)
			{
				snap = _scenes?.ToArray() ?? Array.Empty<Scene>();
				currentScene = _current;
			}

			var activeHandles = currentScene?.Windows.Select(w => (long)w.Handle).ToArray() ?? Array.Empty<long>();
			var sceneEntries = snap.Select(s => new SceneSnapshot.SceneEntry(
				s.Key,
				s.Windows.Select(w => (long)w.Handle).ToArray()
			)).ToArray();

			return new SceneSnapshot.Snapshot(activeHandles, sceneEntries);
		}

		private List<Scene>? RestoreScenesFromSnapshot(string path)
		{
			var snapshot = SceneSnapshot.Load(path);
			if (snapshot is null)
			{
				Log.Info("STARTUP", "Scene snapshot missing or corrupt, falling back to default grouping");
				return null;
			}

			var allWindows = GetSceneableWindows()
				.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
				.ToDictionary(w => (long)w.Handle);

			var claimed = new HashSet<long>();
			var scenes = new List<Scene>();

			foreach (var entry in snapshot.Scenes)
			{
				var validWindows = entry.Handles
					.Where(h => Win32.IsWindow((IntPtr)h) && allWindows.ContainsKey(h))
					.Select(h => { claimed.Add(h); return allWindows[h]; })
					.ToArray();

				if (validWindows.Length > 0)
				{
					scenes.Add(new Scene(entry.Key, validWindows));
					Log.Info("STARTUP", $"Restored scene '{entry.Key}' with {validWindows.Length}/{entry.Handles.Length} windows");
				}
			}

			// Unclaimed windows get default PID grouping
			var unclaimed = allWindows
				.Where(kv => !claimed.Contains(kv.Key))
				.Select(kv => kv.Value);

			var defaultScenes = unclaimed
				.GroupBy(GetWindowGroupKey)
				.Select(g => new Scene(g.Key, g.ToArray()));
			scenes.AddRange(defaultScenes);

			Log.Info("STARTUP", $"Scene restore complete: {scenes.Count} scenes ({claimed.Count} windows restored)");
			return scenes.Count > 0 ? scenes : null;
		}

		public bool IsCurrentScene(Scene? scene) => object.Equals(scene, _current);

		/// <summary>
		/// Re-stows a window off-screen via the active strategy. Used to return a
		/// dragged tray window to its parked state when a sidebar drag is cancelled,
		/// so it stays hidden on stage while the live tile keeps capturing it.
		/// </summary>
		#region Z-order preservation
		// Depth of a window the last time its scene was on screen: 0 = frontmost, larger =
		// further back. Without this a switch rebuilt the stack from Scene.Windows list order,
		// so two overlapping windows swapped which one was on top every time.
		private readonly Dictionary<IntPtr, int> _zDepth = new();
		private readonly object _zDepthLock = new();

		/// <summary>
		/// Records how deep each of <paramref name="windows"/> currently sits in the desktop
		/// z-chain. Walks the chain once front-to-back and stops as soon as every window of
		/// interest has been placed, so the cost is bounded by the windows above the last one.
		/// </summary>
		private void CaptureZOrder(IReadOnlyCollection<IWindow> windows)
		{
			if (windows.Count == 0) return;

			var wanted = new HashSet<IntPtr>(windows.Select(w => w.Handle));
			var found = new Dictionary<IntPtr, int>(wanted.Count);
			var depth = 0;

			for (var h = Win32.GetTopWindow(IntPtr.Zero);
				h != IntPtr.Zero && found.Count < wanted.Count;
				h = Win32.GetWindow(h, Win32.GW.GW_HWNDNEXT))
			{
				if (wanted.Contains(h))
					found[h] = depth++;
			}

			if (found.Count == 0) return;

			lock (_zDepthLock)
			{
				foreach (var (handle, d) in found)
					_zDepth[handle] = d;
			}
		}

		/// <summary>
		/// The scene's windows ordered back-to-front, so a caller that brings each one to the
		/// top in turn ends with the captured stacking. Windows with no captured depth (never
		/// seen on screen — a brand new window, or one added while the scene was hidden) sort
		/// to the back, behind everything whose position is actually known.
		/// </summary>
		private IEnumerable<IWindow> OrderBottomToTop(IEnumerable<IWindow> windows)
		{
			lock (_zDepthLock)
			{
				return windows
					.OrderByDescending(w => _zDepth.TryGetValue(w.Handle, out var d) ? d : int.MaxValue)
					.ToArray();
			}
		}

		private void ForgetZOrder(IntPtr handle)
		{
			lock (_zDepthLock)
				_zDepth.Remove(handle);
		}
		#endregion

		public void ParkWindow(IWindow window) => WindowStrategy.Hide(window);

		/// <summary>
		/// Restores a parked window to its saved on-stage rect (and full alpha). Counterpart
		/// to <see cref="ParkWindow"/>; used to cancel a stage→tray drag.
		/// </summary>
		public void RestoreWindow(IWindow window) => WindowStrategy.Show(window);

		public Scene? CurrentScene => _current;

		// ---- Virtual desktops: each desktop keeps its own groups and its own stage. ----
		private Guid _knownDesktop = Guid.Empty;
		private DateTime _desktopSettleUntil = DateTime.MinValue;
		private readonly Dictionary<Guid, Scene?> _stageByDesktop = new();
		private int _desktopTick;

		/// <summary>Raised on the UI thread after the user switched virtual desktops.</summary>
		public event EventHandler? VirtualDesktopChanged;

		/// <summary>The group name part of a scene key ("pid#group@desk" → "group").</summary>
		public static string GroupOfKey(string key)
		{
			var i = key.IndexOf('#');
			if (i < 0) return "";
			var g = key.Substring(i + 1);
			var at = g.LastIndexOf('@');
			return at >= 0 ? g.Substring(0, at) : g;
		}

		public static bool IsSceneOnCurrentDesktop(Scene scene) =>
			scene.Windows.ToArray().Any(w => VirtualDesktop.IsOnCurrentDesktop(w.Handle));

		/// <summary>
		/// Called by MainWindow's timer (~250 ms). On a desktop switch only bookkeeping happens:
		/// remember what was on stage where the user left, and treat the new desktop's own
		/// remembered stage as current. No window is moved — Windows is already showing the
		/// new desktop exactly as it was left.
		/// </summary>
		public void PollVirtualDesktop()
		{
			var now = VirtualDesktop.CurrentDesktopId;
			if (now == Guid.Empty) return;

			if (_knownDesktop == Guid.Empty)
			{
				_knownDesktop = now;
				AdoptLegacyGroups();
				return;
			}

			if (now != _knownDesktop)
			{
				_stageByDesktop[_knownDesktop] = _current;
				_knownDesktop = now;
				_desktopSettleUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);

				_stageByDesktop.TryGetValue(now, out var saved);
				var prior = _current;
				_current = saved is not null && IsSceneOnCurrentDesktop(saved) ? saved : null;
				Scene[] scenes;
				lock (_scenesLock) scenes = _scenes.ToArray();
				foreach (var s in scenes) s.IsSelected = ReferenceEquals(s, _current);

				Log.Info("VDESK", $"Desktop switched → stage '{_current?.Title ?? "(none)"}'");
				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));
				VirtualDesktopChanged?.Invoke(this, EventArgs.Empty);
				return;
			}

			// Every ~3 s re-read where windows live (Task View moves) — cheap, dispatcher only.
			if (++_desktopTick % 12 == 0)
				VirtualDesktop.Refresh(GetSceneableWindows().ToArray().Select(w => w.Handle));
		}

		/// <summary>
		/// Groups made before per-desktop support go to the desktop where most of their apps
		/// live right now.
		/// </summary>
		private void AdoptLegacyGroups()
		{
			if (!AppGroups.HasLegacy) return;
			var best = GetSceneableWindows().ToArray()
				.GroupBy(w => VirtualDesktop.DesktopOf(w.Handle))
				.OrderByDescending(g => g.Count())
				.Select(g => g.Key)
				.FirstOrDefault();
			AppGroups.AdoptLegacy(best == Guid.Empty ? _knownDesktop : best);
		}

		public bool IsDesktopView => _current is null;

		public IWindow[] GetSceneableWindowsSnapshot() => GetSceneableWindows().ToArray();

		public IEnumerable<IWindow> GetCurrentWindows() => _current?.Windows ?? GetSceneableWindows();

		/// <summary>
		/// Instantly hides all windows in the current scene (alpha→0) without doing a full SwitchTo.
		/// Used by the transition animation so the outgoing placeholder covers the real window.
		/// </summary>
		public void HideCurrentSceneWindows()
		{
			if (_current == null)
			{
				Log.Info("SWITCH", "Pre-hide: no current scene, nothing to hide");
				return;
			}
			var windows = _current.Windows.ToArray();
			Log.Info("SWITCH", $"Pre-hiding {windows.Length} windows in '{_current.Title}' for animation");

			// The animated path parks the outgoing scene here, before SwitchTo runs, so this is
			// the last moment its stacking is readable. Capture it or coming back to this scene
			// finds no depths recorded and falls back to list order.
			CaptureZOrder(windows);

			foreach (var w in windows)
				WindowStrategy.Hide(w);
		}


		/// <summary>
		/// Restores minimized windows in a scene at alpha=0 so they have real screen positions
		/// but are invisible. Prevents the Windows taskbar restore animation on first switch.
		/// </summary>
		public void RestoreMinimizedInvisibly(Scene scene)
		{
			if (scene == null) return;
			foreach (var w in scene.Windows.Where(w => w.IsMinimized && !IsUserMinimized(w)))
			{
				Win32Helper.SetAlpha(w.Handle, 0);
				Log.Window("SWITCH", "Silent restore (minimized→alpha=0)", w);
				w.ShowNormal();
			}
		}

		/// <summary>
		/// Shows desktop icons immediately (used when setting is disabled)
		/// </summary>
		public void ShowDesktopIcons()
		{
			_desktop.ShowIcons();
		}

		/// <summary>
		/// Hides desktop icons immediately (used when setting is enabled)
		/// </summary>
		public void HideDesktopIcons()
		{
			_desktop.HideIcons();
		}

		// Group windows by **process id** instead of the process name so that every
		// newly-launched program (i.e. a new process, even if it shares the same
		// executable name with another instance) gets its **own** scene.
		//
		// This fulfils the requirement that launching a new program should ALWAYS
		// create a separate scene.
		// One scene per app (or remembered split of apps) per user group: a Chrome window given
		// its own group gets its own tile; apps the user combined share one again after restart.
		// Keys also carry the window's virtual desktop: each desktop has its own groups, combos
		// and saved positions, and a tile never spans desktops.
		private string GetWindowGroupKey(IWindow window)
		{
			var desk = VirtualDesktop.DesktopOf(window.Handle);
			using (AppGroups.For(desk))
				return $"{AppGroups.GetSplitKey(window.ProcessFileName) ?? window.ProcessId.ToString()}#{AppGroups.GetEffectiveGroup(window.Handle, window.ProcessFileName, window.Title)}@{desk:N}";
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					// Already handled by Stop() method which should be called explicitly
					// But ensure cleanup in case Dispose is called directly
					Stop();
				}
				_disposed = true;
			}
		}
	}
}
