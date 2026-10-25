using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Object selector of the generic publisher: the <see cref="ObjectField"/> stays the visible
	/// "current selection", and an arrow button opens a searchable popup when several compatible
	/// objects exist (the dropdown behaves like the platform picker: a button anchored popup, not a
	/// native enum field).
	/// </summary>
	public class AssetSelectionField : VisualElement {
		private readonly ObjectField _field;
		private readonly Button      _arrow;

		private Func<IEnumerable<UnityEngine.Object>> _candidates;
		private Func<UnityEngine.Object, string>      _describe;

		public AssetSelectionField(string label, Type objectType) {
			style.flexDirection = FlexDirection.Row;
			style.alignItems    = Align.Center;

			_field = new ObjectField(label) {
				objectType        = objectType,
				allowSceneObjects = true,
				style             = { flexGrow = 1 }
			};

			_field.RegisterValueChangedCallback(evt => {
				if (_syncing)
					return;

				SelectionChanged?.Invoke(evt.newValue);
			});

			_arrow = new Button(Open) {
				text    = "▾",
				tooltip = "Pick among the compatible objects",
				style   = { minWidth = 28, marginLeft = 4 }
			};
			_arrow.AddToClassList("large");

			// The button must be exactly as tall as the field it sits next to.
			_field.RegisterCallback<GeometryChangedEvent>(_ => SyncArrowHeight(_arrow, _field));

			Add(_field);
			Add(_arrow);
		}

		private bool _syncing;

		/// <summary>Raised when the user picks another object (field or popup).</summary>
		public event Action<UnityEngine.Object> SelectionChanged;

		/// <summary>Objects offered by the popup, read each time it opens.</summary>
		public Func<IEnumerable<UnityEngine.Object>> Candidates {
			get => _candidates;
			set => _candidates = value;
		}

		/// <summary>Text shown next to an object in the popup (falls back to its name).</summary>
		public Func<UnityEngine.Object, string> Describe {
			get => _describe;
			set => _describe = value;
		}

		/// <summary>The object shown in the field, without raising <see cref="SelectionChanged"/>.</summary>
		public UnityEngine.Object Value {
			get => _field.value;
			set {
				_syncing = true;
				_field.SetValueWithoutNotify(value);
				_syncing = false;
			}
		}

		public string Label {
			get => _field.label;
			set => _field.label = value;
		}

		/// <summary>
		/// Adds the arrow next to an existing <see cref="ObjectField"/> (the panels keep theirs from
		/// the UXML): the button opens the same searchable popup and reports the chosen object.
		/// </summary>
		public static Button Attach(
			ObjectField field,
			Func<IEnumerable<UnityEngine.Object>> candidates,
			Action<UnityEngine.Object> onSelected,
			Func<UnityEngine.Object, string> describe = null
		) {
			if (field?.parent == null)
				return null;

			var arrow = new Button {
				text    = "▾",
				tooltip = "Pick among the compatible objects",
				style   = { minWidth = 28, marginLeft = 4 }
			};
			arrow.AddToClassList("large");

			// The arrow must sit *next to* the ObjectField, not under it: the field is moved into a row
			// that takes its place in the parent layout (usually a vertical GroupBox).
			var parent = field.parent;
			var index  = parent.IndexOf(field);
			var row    = new VisualElement {
				style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
			};

			parent.Insert(index, row);
			row.Add(field);
			field.style.flexGrow = 1;
			row.Add(arrow);

			// The button must be exactly as tall as the field it sits next to.
			field.RegisterCallback<GeometryChangedEvent>(_ => SyncArrowHeight(arrow, field));

			arrow.clicked += () => {
				var found = candidates?.Invoke()?.Where(o => o != null).ToArray()
					?? Array.Empty<UnityEngine.Object>();

				if (found.Length == 0)
					return;

				var proxy = new Popup(
					found,
					() => field.value,
					describe ?? (o => o?.name),
					o => {
						// Assigning the value raises the ChangeEvent the panel already handles.
						field.value = o;
						onSelected?.Invoke(o);
					}
				);

				UnityEditor.PopupWindow.Show(arrow.worldBound, proxy);
			};

			return arrow;
		}

		/// <summary>Hides the arrow when only one compatible object exists.</summary>
		public void Refresh() {
			var count = Candidates?.Invoke()?.Count() ?? 0;
			_arrow.style.display = count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		/// <summary>Copies the measured height of <paramref name="field"/> onto <paramref name="arrow"/>.</summary>
		private static void SyncArrowHeight(VisualElement arrow, VisualElement field) {
			var height = field.layout.height;

			if (arrow == null || float.IsNaN(height) || height <= 0f)
				return;

			arrow.style.height = height;
		}

		private void Open() {
			if (!enabledSelf)
				return;

			var candidates = Candidates?.Invoke()?.Where(o => o != null).ToArray()
				?? Array.Empty<UnityEngine.Object>();

			if (candidates.Length == 0)
				return;

			UnityEditor.PopupWindow.Show(_arrow.worldBound, new Popup(
				candidates,
				() => Value,
				DescribeObject,
				obj => {
					Value = obj;
					SelectionChanged?.Invoke(obj);
				}
			));
		}

		private string DescribeObject(UnityEngine.Object obj)
			=> _describe?.Invoke(obj) ?? obj?.name;

		/// <summary>Searchable list of the compatible objects.</summary>
		private sealed class Popup : PopupWindowContent {
			private const float RowHeight = 22f;

			private readonly UnityEngine.Object[]             _candidates;
			private readonly Func<UnityEngine.Object>         _current;
			private readonly Func<UnityEngine.Object, string> _describe;
			private readonly Action<UnityEngine.Object>       _select;
			private readonly List<UnityEngine.Object>          _visible = new();

			public Popup(
				UnityEngine.Object[] candidates,
				Func<UnityEngine.Object> current,
				Func<UnityEngine.Object, string> describe,
				Action<UnityEngine.Object> select
			) {
				_candidates = candidates;
				_current    = current;
				_describe   = describe;
				_select     = select;
				_visible.AddRange(candidates);
			}

			public override Vector2 GetWindowSize()
				=> new(320f, Mathf.Min(360f, (_visible.Count * RowHeight) + 48f));

			public override void OnGUI(Rect rect) { }

			public override VisualElement CreateGUI() {
				var root = new VisualElement {
					style = {
						paddingTop    = 6,
						paddingBottom = 6,
						paddingLeft   = 8,
						paddingRight  = 8,
						flexGrow      = 1
					}
				};

				var search = new ToolbarSearchField { style = { marginBottom = 6 } };
				var list   = new ScrollView { style = { flexGrow = 1 } };

				root.Add(search);
				root.Add(list);

				void Rebuild(string filter) {
					list.Clear();
					_visible.Clear();

					foreach (var candidate in _candidates) {
						var text = _describe(candidate) ?? string.Empty;

						if (!string.IsNullOrEmpty(filter)
						    && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
							continue;

						_visible.Add(candidate);

						var current = ReferenceEquals(candidate, _current());
						var button  = new Button(() => {
							_select(candidate);
							editorWindow?.Close();
						}) {
							text  = text,
							style = {
								height          = RowHeight,
								unityTextAlign  = TextAnchor.MiddleLeft,
								backgroundColor = current
									? new Color(0.30f, 0.45f, 0.65f)
									: new Color(0f, 0f, 0f, 0f)
							}
						};

						list.Add(button);
					}

					if (_visible.Count == 0)
						list.Add(new Label("No compatible object.") { style = { opacity = 0.7f } });
				}

				search.RegisterValueChangedCallback(evt => Rebuild(evt.newValue));
				Rebuild(null);

				return root;
			}
		}
	}
}
