using System;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Foldout whose open/closed state is persisted per panel and section
	/// (<see cref="AssetPublishSettings.Foldout"/>), so the publisher reopens as the user left it.
	/// </summary>
	public class AssetPublishFoldout : Foldout {
		private readonly string _panel;
		private readonly string _section;

		public AssetPublishFoldout(string panel, string section, string title, bool fallback = true) {
			_panel   = panel;
			_section = section;

			// Shared look of the editor sections: padded heading, separator, content aligned with the
			// panel (see nox.cck/Resources/styles/section.uss).
			AddToClassList("section");
			AddToClassList("foldout-flush");
			AddToClassList("large");

			text  = title;
			value = AssetPublishSettings.Foldout(panel, section, fallback);

			this.RegisterValueChangedCallback(evt => {
				if (_panel == null || _section == null)
					return;

				AssetPublishSettings.SetFoldout(_panel, _section, evt.newValue);
			});
		}

		/// <summary>
		/// Moves <paramref name="content"/> inside a persisted foldout. Used to turn an existing
		/// section of a UXML panel into a collapsible one without touching the layout.
		/// </summary>
		public static AssetPublishFoldout Wrap(
			VisualElement content,
			string panel,
			string section,
			string title,
			bool fallback = true
		) {
			if (content == null)
				return null;

			var foldout = new AssetPublishFoldout(panel, section, title, fallback);

			content.RemoveFromHierarchy();
			foldout.Add(content);

			return foldout;
		}

		/// <summary>
		/// Wraps the nearest <see cref="GroupBox"/> of <paramref name="element"/> in a persisted
		/// foldout, in place (same parent, same index). The UXML sections keep their layout.
		/// </summary>
		public static AssetPublishFoldout WrapSection(
			VisualElement element,
			string panel,
			string section,
			string title,
			bool fallback = true
		) {
			var box = element as GroupBox ?? element?.GetFirstAncestorOfType<GroupBox>();

		if (box?.parent == null)
			return null;

			var parent = box.parent;
			var index  = parent.IndexOf(box);

			var foldout = Wrap(box, panel, section, title, fallback);

			if (foldout == null)
				return null;

			parent.Insert(index, foldout);
			return foldout;
		}
	}
}
