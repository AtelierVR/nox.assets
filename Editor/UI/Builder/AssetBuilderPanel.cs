using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Language;
using Nox.CCK.Utils;
using Nox.Editor.Panel;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using IPanel = Nox.Editor.Panel.IPanel;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Generic builder panel: selector, notifications, platforms, output folder, build button and the
	/// progress/result overlays. The asset type only supplies an <see cref="AssetBuilderSource"/>.
	/// </summary>
	public class AssetBuilderPanel : IPanel {
		private readonly AssetBuilderSource _source;

		public AssetBuilderPanel(AssetBuilderSource source)
			=> _source = source ?? throw new ArgumentNullException(nameof(source));

		public AssetBuilderInstance Instance { get; internal set; }

		public AssetBuilderSource Source
			=> _source;

		public string[] GetPath()
			=> _source.Path;

		public IInstance[] GetInstances()
			=> Instance != null ? new IInstance[] { Instance } : Array.Empty<IInstance>();

		/// <summary>
		/// Menu entry: the path given by the mod, Title-cased (<c>avatar/builder</c> ⇒
		/// <c>Avatar/Builder</c>). The path itself stays a stable lowercase identifier.
		/// </summary>
		public string GetLabel()
			=> AssetPublisherPanel.ToMenuLabel(GetPath());

		public IInstance Instantiate(IWindow window, Dictionary<string, object> data) {
			if (Instance != null)
				throw new InvalidOperationException($"{_source.Panel}: the builder panel only supports a single instance.");

			return Instance = new AssetBuilderInstance(this, window, _source);
		}
	}

	/// <summary>One opened builder panel, built from its <see cref="AssetBuilderSource"/>.</summary>
	public class AssetBuilderInstance : IInstance {
		private const string LogTag = "AssetsBuilderUI";

		private readonly IPanel              _panel;
		private readonly IWindow             _window;
		private readonly AssetBuilderSource  _source;

		private VisualElement _root;
		private ObjectField   _selection;
		private VisualElement _notifications;
		private VisualElement _noSelection;
		private VisualElement _allGood;
		private int           _notificationCount;
		private TextField     _output;
		private PlatformPopupField _platforms;
		private Button        _build;

		private VisualElement _buildingContainer;
		private Label         _buildingStatus;
		private ProgressBar   _buildingProgress;

		private VisualElement _resultContainer;
		private Label         _resultSuccess;
		private Label         _resultFailed;
		private Label         _resultDetails;

		private bool _building;

		public AssetBuilderInstance(IPanel panel, IWindow window, AssetBuilderSource source) {
			_panel  = panel;
			_window = window;
			_source = source;

			AssetNotificationHelper.OnNotificationsChanged.AddListener(OnNotificationsChanged);
		}

		// --- IInstance ---------------------------------------------------------------------

		public IPanel GetPanel()
			=> _panel;

		public IWindow GetWindow()
			=> _window;

		public string GetTitle()
			=> _source.Title;

		public IToolOption[] GetOptions()
			=> _source.Options(_window);

		public void OnDestroy() {
			AssetNotificationHelper.OnNotificationsChanged.RemoveListener(OnNotificationsChanged);
			LanguageManager.OnLanguageChanged.RemoveListener(OnLanguageChanged);

			if (_panel is AssetBuilderPanel provider)
				provider.Instance = null;
		}

		private void OnLanguageChanged(string language)
			=> AssetLocalization.Refresh(_root);

		public VisualElement GetContent() {
			if (_root != null)
				return _root;

			_root = new VisualElement { style = { flexGrow = 1 } };

			// --- Selector -------------------------------------------------------------------
			var header = new GroupBox { style = { marginLeft = 0, marginRight = 0, marginTop = 0 } };
			header.AddToClassList("p-8");
			// Rule under the selection: always visible, it closes the header section.
			header.AddToClassList("border-b");

			_selection = new ObjectField(_source.Label) {
				objectType        = _source.DescriptorType,
				allowSceneObjects = true
			};
			_selection.AddToClassList("large");
			// Hides the ObjectField's own selector icon (nox.style.uss `.readonly .unity-object-field__selector`):
			// the arrow attached by AssetSelectionField is the picker.
			_selection.AddToClassList("readonly");
			_selection.value = _source.Selection;
			_selection.RegisterValueChangedCallback(evt => {
				_source.Selection = evt.newValue;
				RefreshState();
			});

			header.Add(_selection);
			AssetSelectionField.Attach(_selection, () => _source.Candidates(), null, candidate => _source.Describe(candidate));
			_root.Add(header);

			// --- Body -----------------------------------------------------------------------
			var scroll = new ScrollView { style = { flexGrow = 1 } };
			// Lets the content fill the viewport, so the empty state can be centred vertically.
			scroll.AddToClassList("scroll-full");

			_notifications = new VisualElement();
			_notifications.AddToClassList("p-8");
			scroll.Add(_notifications);

			_noSelection = new VisualElement {
				style = { flexGrow = 1, justifyContent = Justify.Center, display = DisplayStyle.None }
			};
			_noSelection.AddToClassList("p-8");

			var noSelectionTitle = AssetLocalization.Label("assets.builder.no_selection.title", _source.Label);
			noSelectionTitle.AddToClassList("text-lg");
			noSelectionTitle.AddToClassList("text-center");
			noSelectionTitle.AddToClassList("mb-8");

			var noSelectionDetail = AssetLocalization.Label("assets.builder.no_selection.detail", _source.Label.ToLowerInvariant());
			noSelectionDetail.AddToClassList("text-center");

			_noSelection.Add(noSelectionTitle);
			_noSelection.Add(noSelectionDetail);
			scroll.Add(_noSelection);

			// Nothing to report: the panel says so instead of staying blank.
			_allGood = new VisualElement {
				style = { flexGrow = 1, justifyContent = Justify.Center, display = DisplayStyle.None }
			};
			_allGood.AddToClassList("p-8");

			var allGoodTitle = AssetLocalization.Label("assets.builder.all_good.title");
			allGoodTitle.AddToClassList("text-lg");
			allGoodTitle.AddToClassList("text-center");
			allGoodTitle.AddToClassList("mb-8");

			var allGoodDetail = AssetLocalization.Label("assets.builder.all_good.detail", _source.Label.ToLowerInvariant());
			allGoodDetail.AddToClassList("text-center");

			_allGood.Add(allGoodTitle);
			_allGood.Add(allGoodDetail);
			scroll.Add(_allGood);

			_root.Add(scroll);

			// --- Footer: output folder, platforms and build (bottom of the panel) ------------
			var footer = new GroupBox { style = { marginLeft = 0, marginRight = 0, marginBottom = 0 } };
			footer.AddToClassList("p-8");
			footer.AddToClassList("border-t");

			// Output folder
			var outputRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
			outputRow.AddToClassList("mb-8");

			_output = new TextField { value = OutputFolder, style = { flexGrow = 1, marginRight = 4 } };
			_output.AddToClassList("large");
			_output.RegisterValueChangedCallback(evt => OutputFolder = evt.newValue);
			outputRow.Add(_output);

			var select = AssetLocalization.Button(SelectOutput, "assets.common.select");
			select.AddToClassList("large");
			outputRow.Add(select);

			var open = AssetLocalization.Button(OpenOutput, "assets.common.open");
			open.AddToClassList("large");
			open.style.marginLeft = 4;
			outputRow.Add(open);

			footer.Add(outputRow);

			// Platforms + Build
			var buildRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

			_platforms = new PlatformPopupField { style = { flexGrow = 1 } };
			_platforms.value = _source.Platforms;
			_platforms.RegisterValueChangedCallback(evt => {
				_source.Platforms = evt.newValue;
				RefreshState();
			});
			buildRow.Add(_platforms);

			_build = AssetLocalization.Button(() => BuildAsync().Forget(), "assets.builder.build");
			_build.style.minWidth = 134;
			_build.style.marginLeft = 4;
			_build.AddToClassList("large");
			buildRow.Add(_build);

			footer.Add(buildRow);
			_root.Add(footer);

			// --- Progress / result ----------------------------------------------------------
			_buildingContainer = new VisualElement { style = { display = DisplayStyle.None } };
			_buildingContainer.AddToClassList("p-8");

			_buildingStatus = AssetLocalization.Label("assets.builder.preparing");
			_buildingProgress = new ProgressBar { value = 0 };
			_buildingProgress.AddToClassList("large");

			_buildingContainer.Add(_buildingStatus);
			_buildingContainer.Add(_buildingProgress);
			_root.Add(_buildingContainer);

			_resultContainer = new VisualElement { style = { display = DisplayStyle.None } };
			_resultContainer.AddToClassList("p-8");

			_resultSuccess = AssetLocalization.Label("assets.builder.built");
			_resultSuccess.style.display = DisplayStyle.None;
			_resultSuccess.AddToClassList("text-lg");
			_resultFailed = AssetLocalization.Label("assets.builder.failed");
			_resultFailed.style.display = DisplayStyle.None;
			_resultFailed.AddToClassList("text-lg");

			_resultDetails = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 8 } };

			_resultContainer.Add(_resultSuccess);
			_resultContainer.Add(_resultFailed);
			_resultContainer.Add(_resultDetails);
			_root.Add(_resultContainer);

			OnNotificationsChanged(AssetNotificationHelper.Notifications.ToArray());
			RefreshState();

			LanguageManager.OnLanguageChanged.AddListener(OnLanguageChanged);

			return _root;
		}

		// --- State -------------------------------------------------------------------------

		private string OutputFolder {
			get => AssetPublishSettings.Get(_source.Panel, "output_folder", _source.DefaultOutputFolder);
			set => AssetPublishSettings.Set(_source.Panel, "output_folder", value);
		}

		private void RefreshState() {
			var selection = _source.Selection;

			if (_noSelection != null)
				_noSelection.style.display = selection == null ? DisplayStyle.Flex : DisplayStyle.None;

			// « All good » only makes sense with a selection: otherwise the empty state is shown.
			if (_allGood != null)
				_allGood.style.display = selection != null && _notificationCount == 0
					? DisplayStyle.Flex
					: DisplayStyle.None;

			_platforms?.SetValueWithoutNotify(_source.Platforms);
			_platforms?.SetEnabled(selection != null);
			_selection?.SetValueWithoutNotify(selection);

			if (_build != null)
				_build.SetEnabled(!_building && selection != null && AssetNotificationHelper.Allowed);
		}

		private void OnNotificationsChanged(AssetNotification[] notifications) {
			if (_notifications == null)
				return;

			_notifications.Clear();

			var prefab = Resources.Load<VisualTreeAsset>("nox.cck.notification");

			foreach (var notification in notifications) {
				if (notification?.Content is not { Length: > 0 })
					continue;

				var element = prefab != null ? prefab.CloneTree() : new VisualElement();
				element.AddToClassList("notification-" + notification.Type.ToString().ToLowerInvariant());

				var content = element.Q<VisualElement>("content") ?? element;

				content.Clear();
				content.Add(new Label(LanguageManager.Get(notification.Content[0], notification.Content[1..])) {
					style = {
						flexWrap        = Wrap.Wrap,
						whiteSpace      = WhiteSpace.Normal,
						unityTextAlign  = TextAnchor.MiddleLeft,
						flexGrow        = 1
					}
				});

				var actions = element.Q<VisualElement>("actions");

				if (actions != null) {
					actions.Clear();

					foreach (var action in notification.Actions)
						actions.Add(new Button(() => action.Action.Invoke()) {
							text = LanguageManager.Get(action.Content[0], action.Content[1..])
						});
				}

				_notifications.Add(element);
			}

			_notificationCount = _notifications.childCount;
			RefreshState();
		}

		// --- Actions -----------------------------------------------------------------------

		private void SelectOutput() {
			var path = EditorUtility.OpenFolderPanel("Select output folder", string.Empty, string.Empty);

			if (string.IsNullOrEmpty(path))
				return;

			// Keep the path relative to the project so it survives a machine change.
			var dataPath = Application.dataPath;

			if (path.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
				path = "Assets" + path[dataPath.Length..];

			_output.SetValueWithoutNotify(path);
			OutputFolder = path;
		}

		private void OpenOutput() {
			var folder = _output?.value;

			if (string.IsNullOrEmpty(folder))
				return;

			if (!System.IO.Directory.Exists(folder))
				System.IO.Directory.CreateDirectory(folder);

			Application.OpenURL(folder);
		}

		private async UniTask BuildAsync() {
			if (_building)
				return;

			var error = _source.Prepare?.Invoke();

			if (!string.IsNullOrEmpty(error)) {
				ShowResult(false, error);
				return;
			}

			_building = true;
			_build.SetEnabled(false);
			_resultContainer.style.display   = DisplayStyle.None;
			_buildingContainer.style.display = DisplayStyle.Flex;
			_buildingProgress.value          = 0;
			_buildingStatus.text             = "Building...";

			try {
				var context = new AssetBuildContext(
					_output?.value ?? _source.DefaultOutputFolder,
					_source.Platforms,
					EngineExtensions.CurrentEngine,
					_source.Selection,
					(progress, message) => {
						_buildingProgress.value = Mathf.Clamp01(progress) * 100f;
						_buildingStatus.text    = message;
					}
				);

				var result = await _source.Build(context);

				_buildingContainer.style.display = DisplayStyle.None;

				if (result == null || !result.Success) {
					ShowResult(false, result?.Message ?? "The build returned no result.");
					return;
				}

				ShowResult(true, string.Join("\n", result.Outputs.Select(output => $"{output.Key.GetPlatformName()}: {output.Value}")));
			} catch (Exception ex) {
				_buildingContainer.style.display = DisplayStyle.None;
				Logger.LogError(new Exception("Build failed", ex), tag: LogTag);
				ShowResult(false, ex.Message);
			} finally {
				_building = false;
				_source.OnBuildFinished?.Invoke();
				RefreshState();
			}
		}

		private void ShowResult(bool success, string details) {
			_buildingContainer.style.display = DisplayStyle.None;
			_resultContainer.style.display   = DisplayStyle.Flex;
			_resultSuccess.style.display     = success ? DisplayStyle.Flex : DisplayStyle.None;
			_resultFailed.style.display      = success ? DisplayStyle.None : DisplayStyle.Flex;
			_resultDetails.text              = details;
		}
	}
}
