using System;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// One information row of the generic publisher: a label and a value the mod reads (and
	/// optionally writes). World capacity, owner, release channel… are declared by the mod and
	/// rendered by <see cref="AssetPublisherInstance"/> without it knowing anything about them.
	/// <para>
	/// The row keeps the same layout and classes as <c>Name</c>/<c>Description</c>: fixed-width label
	/// on the left, large field on the right. The <b>type</b> is chosen by the source
	/// (<see cref="ReadOnly"/>, <see cref="Text"/>, <see cref="Unsigned"/>, <see cref="Toggle"/>), so a
	/// capacity is edited with a numeric field — <c>0</c> meaning unlimited — never with free text.
	/// </para>
	/// </summary>
	public sealed class AssetInfoEntry {
		private enum EntryKind {
			ReadOnly,
			Text,
			Unsigned,
			Toggle
		}

		private readonly EntryKind _kind;

		private AssetInfoEntry(EntryKind kind, string label, Func<string> get, string tooltip) {
			_kind    = kind;
			Label    = label;
			Get      = get;
			Tooltip  = tooltip;
		}

		/// <summary>Label shown on the left of the row.</summary>
		public string Label { get; }

		/// <summary>Current value as text (read-only rows and diagnostics).</summary>
		public Func<string> Get { get; }

		/// <summary>Hint appended after the field (<c>0 = unlimited</c>…).</summary>
		public string Hint { get; private set; }

		public string Tooltip { get; }

		/// <summary>Whether the user can change the value.</summary>
		public bool Editable
			=> _kind != EntryKind.ReadOnly;

		private Func<uint> GetUnsigned { get; set; }

		private Action<uint> SetUnsigned { get; set; }

		private Func<bool> GetToggle { get; set; }

		private Action<bool> SetToggle { get; set; }

		private Func<string> GetText { get; set; }

		private Action<string> SetText { get; set; }

		/// <summary>Entry the user cannot change (owner, id, server…).</summary>
		public static AssetInfoEntry ReadOnly(string label, Func<string> get, string tooltip = null)
			=> new(EntryKind.ReadOnly, label, get, tooltip);

		/// <summary>Free text entry; <paramref name="set"/> stores the typed value.</summary>
		public static AssetInfoEntry Text(string label, Func<string> get, Action<string> set, string tooltip = null)
			=> new(EntryKind.Text, label, get, tooltip) {
				GetText = get,
				SetText = set
			};

		/// <summary>
		/// Unsigned entry (capacity, count…): by convention <c>0</c> means « unlimited », so the hint is
		/// shown next to the field unless another one is given.
		/// </summary>
		public static AssetInfoEntry Unsigned(
			string label,
			Func<uint> get,
			Action<uint> set,
			string hint = "0 = unlimited",
			string tooltip = null
		) => new(EntryKind.Unsigned, label, () => (get?.Invoke() ?? 0u).ToString(), tooltip) {
			GetUnsigned = get,
			SetUnsigned = set,
			Hint        = hint
		};

		/// <summary>Boolean entry rendered as a <see cref="Toggle"/>.</summary>
		public static AssetInfoEntry Toggle(string label, Func<bool> get, Action<bool> set, string tooltip = null)
			=> new(EntryKind.Toggle, label, () => (get?.Invoke() ?? false).ToString(), tooltip) {
				GetToggle = get,
				SetToggle = set
			};

		/// <summary>Row of the form: label on the left, field on the right (same look as Name).</summary>
		public VisualElement Build() {
			// The hint belongs to the field: it is rendered *under* it, indented by the label column so
			// it stays aligned with the input.
			var container = new VisualElement { style = { marginBottom = 8 } };

			var row = new VisualElement {
				style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
			};

			row.Add(new Label(Label) { style = { minWidth = LabelWidth } });

			switch (_kind) {
				case EntryKind.ReadOnly:
					row.Add(new Label(Get?.Invoke() ?? string.Empty) { style = { flexGrow = 1 } });
					break;

				case EntryKind.Text: {
					var field = new TextField { value = GetText?.Invoke() ?? string.Empty };
					field.AddToClassList("large");
					field.style.flexGrow = 1;
					field.RegisterValueChangedCallback(evt => Notify(() => SetText?.Invoke(evt.newValue)));
					row.Add(field);
					break;
				}

				case EntryKind.Unsigned: {
					var field = new UnsignedIntegerField { value = GetUnsigned?.Invoke() ?? 0u };
					field.AddToClassList("large");
					field.style.flexGrow = 1;
					field.RegisterValueChangedCallback(evt => Notify(() => SetUnsigned?.Invoke(evt.newValue)));
					row.Add(field);
					break;
				}

				case EntryKind.Toggle: {
					var toggle = new Toggle { value = GetToggle?.Invoke() ?? false };
					toggle.AddToClassList("large");
					toggle.style.flexGrow = 1;
					toggle.RegisterValueChangedCallback(evt => Notify(() => SetToggle?.Invoke(evt.newValue)));
					row.Add(toggle);
					break;
				}
			}

			if (!string.IsNullOrEmpty(Tooltip))
				row.tooltip = Tooltip;

			container.Add(row);

			if (!string.IsNullOrEmpty(Hint)) {
				var hint = new Label(Hint) { style = { marginLeft = LabelWidth, marginTop = 2, opacity = 0.6f } };
				hint.AddToClassList("text-sm");
				container.Add(hint);
			}

			return container;
		}

		/// <summary>Width of the label column, shared with the rows of the form.</summary>
		private const int LabelWidth = 100;

		private static void Notify(Action action) {
			try {
				action();
			} catch {
				// The mod reports its own errors; a row must never break the form.
			}
		}
	}
}
