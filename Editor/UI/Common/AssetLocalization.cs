using System;
using Nox.CCK.Editor;
using Nox.CCK.Language;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Helpers to build the localized elements of the asset panels: every text is a
	/// <see cref="LocalizedLabel"/> / <see cref="LocalizedButton"/> / <see cref="LocalizedFoldout"/>
	/// with a <c>lang-key</c>, so it follows the language without rebuilding the UI.
	/// <para>
	/// <see cref="Refresh"/> re-applies every key of a tree; the panels call it once when they are
	/// built and again on <see cref="LanguageManager.OnLanguageChanged"/>.
	/// </para>
	/// </summary>
	public static class AssetLocalization {
		/// <summary>Localized <see cref="Label"/>.</summary>
		public static LocalizedLabel Label(string key, params object[] args)
			=> new() { LangKey = key, LangArgs = args };

		/// <summary>Localized <see cref="Button"/>.</summary>
		public static LocalizedButton Button(Action clicked, string key, params object[] args) {
			// LocalizedButton is a UxmlElement: it only exposes the parameterless constructor.
			var button = new LocalizedButton { LangKey = key, LangArgs = args };

			if (clicked != null)
				button.clicked += clicked;

			return button;
		}

		/// <summary>Text of a key, formatted with its arguments.</summary>
		public static string Text(string key, params object[] args)
			=> LanguageManager.Get(key, args);

		/// <summary>Re-applies the <c>lang-key</c> of every element of <paramref name="root"/>.</summary>
		public static void Refresh(VisualElement root) {
			if (root == null)
				return;

			if (root is ILocalizedElement localized)
				localized.RefreshText();

			foreach (var child in root.Children())
				Refresh(child);
		}
	}
}
