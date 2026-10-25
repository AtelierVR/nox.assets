using System;
using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Multi-select platform picker: displays the selected platforms and opens a popup window of
	/// toggles. An asset publishes one variant (build + file) per selected platform, so this is a
	/// multi-selection; build targets the editor cannot compile for are listed but disabled, and an
	/// empty selection means the current platform.
	/// <para>
	/// Shared by every publisher: the avatar and world panels used to carry their own copy.
	/// </para>
	/// </summary>
	public class PlatformPopupField : VisualElement, INotifyValueChanged<Platform[]> {
		private readonly Button _button = new();
		private Platform[]      _value  = Array.Empty<Platform>();

		public PlatformPopupField() {
			AddToClassList("platform-popup-field");

			_button.AddToClassList("large");
			_button.style.alignSelf = Align.Stretch;
			_button.tooltip        = "Platforms this asset publishes: one variant per selection. Empty uses the current platform.";
			_button.clicked        += Open;

			Add(_button);
			UpdateLabel();
		}

		public Platform[] value {
			get => _value;
			set => SetValue(value);
		}

		public void SetValueWithoutNotify(Platform[] newValue) {
			_value = Normalize(newValue);
			UpdateLabel();
		}

		/// <summary>
		/// Popup listing every platform as a toggle; an installed build target can be selected, the
		/// others stay visible but disabled.
		/// </summary>
		private void Open() {
			if (!enabledSelf)
				return;

			UnityEditor.PopupWindow.Show(_button.worldBound, new Content(this));
		}

		private void SetValue(Platform[] newValue) {
			var previous = _value;
			SetValueWithoutNotify(newValue);

			if (previous.SequenceEqual(_value))
				return;

			using var evt = ChangeEvent<Platform[]>.GetPooled(previous, _value);
			evt.target = this;
			SendEvent(evt);
		}

		/// <summary>Selected platforms, deduplicated and ordered like <see cref="PlatformExtensions.All"/>.</summary>
		private static Platform[] Normalize(IEnumerable<Platform> platforms)
			=> (platforms ?? Array.Empty<Platform>())
				.Where(platform => platform != Platform.None)
				.Distinct()
				.OrderBy(platform => Array.IndexOf(PlatformExtensions.All, platform))
				.ToArray();

		private void UpdateLabel()
			=> _button.text = _value.Length == 0
				? $"Current platform ({PlatformExtensions.CurrentPlatform.Display})"
				: string.Join(", ", _value.Select(platform => platform.Display));

		private class Content : PopupWindowContent {
			private const int RowHeight = 20;

			private readonly PlatformPopupField _field;

			public Content(PlatformPopupField field)
				=> _field = field;

			public override Vector2 GetWindowSize()
				=> new(240f, (Selectable.Count() * RowHeight) + 34f);

			private static IEnumerable<Platform> Selectable
				=> PlatformExtensions.All.Where(platform => platform != Platform.None);

			// UI Toolkit content: only CreateGUI is used.
			public override void OnGUI(Rect rect) { }

			public override VisualElement CreateGUI() {
				var root = new VisualElement {
					style = {
						paddingTop    = 6,
						paddingBottom = 6,
						paddingLeft   = 8,
						paddingRight  = 8
					}
				};

				foreach (var platform in Selectable) {
					var supported = platform.IsSupported();

					var toggle = new Toggle(platform.Display) {
						value = _field._value.Contains(platform),
						style = { height = RowHeight }
					};

					toggle.SetEnabled(supported);
					toggle.tooltip = supported
						? $"Publish a {platform.Display} variant"
						: $"{platform.Display} is not installed for this editor";

					toggle.RegisterValueChangedCallback(evt => {
						var selection = _field._value.ToList();

						if (evt.newValue) {
							if (!selection.Contains(platform))
								selection.Add(platform);
						} else
							selection.Remove(platform);

						_field.SetValue(selection.ToArray());
					});

					root.Add(toggle);
				}

				root.Add(new Label("Empty publishes the current platform only.") {
					style = {
						marginTop  = 6,
						opacity    = 0.7f,
						whiteSpace = WhiteSpace.Normal
					}
				});

				return root;
			}
		}
	}
}
