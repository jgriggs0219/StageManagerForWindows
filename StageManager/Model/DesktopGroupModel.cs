using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace StageManager.Model
{
	/// <summary>
	/// One other virtual desktop in the sidebar's lower section: its name and the apps
	/// running on it. Rebuilt wholesale when anything changes (see
	/// MainWindow.RefreshOtherDesktops), so no change notification is needed.
	/// </summary>
	public class DesktopGroupModel
	{
		public Guid Id { get; init; }
		public string Name { get; init; } = "";
		public IReadOnlyList<DesktopAppModel> Apps { get; init; } = Array.Empty<DesktopAppModel>();
	}

	/// <summary>An app (scene) on another desktop. Clicking it jumps to that desktop with it in front.</summary>
	public class DesktopAppModel
	{
		public string Title { get; init; } = "";
		public ImageSource? Icon { get; init; }
		public IntPtr Handle { get; init; }
	}
}
