using System;
using Nox.CCK.Editor;
using Nox.CCK.Language;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Foldout whose open/closed state is persisted per panel and section
	/// (<see cref="AssetPublishSettings.Foldout"/>), so the publisher reopens as the user left it.
	/// Its title is always a language entry (<see cref="ILocalizedElement"/>): there is no literal
	/// fallback, a missing key shows as <c>[key]</c> in the language manager.
	/// </summary>
	public class AssetPublishFoldout : Foldout, ILocalizedElement {
		private readonly string _panel;
		private readonly string _section;

		private string _langKey;

		public AssetPublishFoldout(
			string panel,
			string section,
			string langKey,
			bool defaultExpanded = true,
			params object[] args
		) {
			_panel   = panel;
			_section = section;

			// Shared look of the editor sections: padded heading, separator, content aligned with the
			// panel (see nox.cck/Resources/styles/section.uss).
			AddToClassList("section");
			AddToClassList("foldout-flush");
			AddToClassList("large");

			LangKey = langKey;
			value   = AssetPublishSettings.Foldout(panel, section, defaultExpanded);

			if (args is { Length: > 0 })
				LangArgs = args;

			this.RegisterValueChangedCallback(evt => {
				if (_panel == null || _section == null)
					return;

				AssetPublishSettings.SetFoldout(_panel, _section, evt.newValue);
			});
		}

		/// <summary>Key of the language entry shown as title.</summary>
		public string LangKey {
			get => _langKey;
			set {
				_langKey = value;
				RefreshText();
			}
		}

		/// <summary>Arguments formatted into the entry (kept in <c>userData</c>).</summary>
		public object[] LangArgs {
			get => userData as object[];
			set {
				userData = value;
				RefreshText();
			}
		}

		public void RefreshText() {
			if (!string.IsNullOrEmpty(_langKey))
				text = LanguageManager.Get(_langKey, LangArgs);
		}
	}
}
