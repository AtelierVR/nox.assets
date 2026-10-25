using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Convertors;
using Nox.CCK.Mods.Events;
using Nox.CCK.Utils;
using Nox.Editor.Panel;
using Nox.Network.Assets;
using Nox.Users;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Logger = Nox.CCK.Utils.Logger;
using IPanel = Nox.Editor.Panel.IPanel;

namespace Nox.Assets.Editor {
	/// <summary>
	/// The publisher panel, generic for every asset type: it is built from an
	/// <see cref="AssetPublisherSource"/> (what a mod declares) and drives the shared pipeline
	/// (attach, update, images, version policy, publish).
	/// <para>
	/// A mod therefore only registers a panel and a source: no UI code of its own.
	/// </para>
	/// </summary>
	public class AssetPublisherInstance : IInstance {
		private const string LogTag = "AssetsPublisherUI";

		private readonly IPanel               _panel;
		private readonly IWindow              _window;
		private readonly AssetPublisherSource _source;

		private VisualElement _root;
		private VisualElement _body;
		private VisualElement _notLogged;
		private VisualElement _noSelection;
		private VisualElement _attachSection;
		private VisualElement _attachedSection;

		private ObjectField           _selection;
		private TextField             _attachId;
		private TextField             _attachServer;
		private TextField             _infoName;
		private TextField             _infoDescription;
		private TextField             _infoServer;
		private TextField             _infoId;
		private UnsignedIntegerField  _version;
		private TextField             _releaseNote;
		private VisualElement         _releases;
		private Button                _saveNote;
		private PlatformPopupField    _platforms;
		private Button                _publish;
		private AssetImageGrid        _images;

		private VisualElement _progressContainer;
		private ProgressBar   _progress;
		private Label         _status;
		private Label         _result;

		private bool _publishing;

		/// <summary>Selection the auto-attach was already attempted for (avoid retry loops).</summary>
		private UnityEngine.Object _autoAttachTried;

		/// <summary><c>server:id</c> of the asset the grid was last loaded for.</summary>
		private string _imagesBoundFor;

		/// <summary>« asset@version » the release check already ran for.</summary>
		private string _releaseCheckedFor;

		/// <summary>Guards <see cref="RefreshSelection"/> against re-entrance (it calls RefreshAttached).</summary>
		private bool _refreshing;

		/// <summary>Subscription to the <c>user_updated</c> event, so the form follows login state.</summary>
		private EventSubscription _userSubscription;

		/// <summary>Guards the one-shot current-user fetch.</summary>
		private bool _fetchingUser;

		/// <summary>Identifier of the user the form was last refreshed for (<c>""</c> = nobody).</summary>
		private string _lastUser;

		/// <summary>Next allowed attempt of <see cref="EnsureUserAsync"/>.</summary>
		private double _nextUserFetch;

		public AssetPublisherInstance(IPanel panel, IWindow window, AssetPublisherSource source) {
			_panel  = panel;
			_window = window;
			_source = source ?? throw new ArgumentNullException(nameof(source));

			_source.AttachedChanged += _ => RefreshSelection();
		}

		// ---------------------------------------------------------------------------------------
		// IInstance
		// ---------------------------------------------------------------------------------------

		public IPanel GetPanel()
			=> _panel;

		public IWindow GetWindow()
			=> _window;

		public string GetTitle()
			=> _source.Title;

		public IToolOption[] GetOptions()
			=> _source.Options(_window);

		public void OnDestroy() {
			EditorApplication.update -= OnEditorUpdate;

			if (_userSubscription != null) {
				Editor.CoreAPI?.EventAPI.Unsubscribe(_userSubscription);
				_userSubscription = null;
			}

			AssetPublisherNotifications.Clear(_source);

			if (_panel is Nox.Assets.Editor.AssetPublisherPanel provider)
				provider.Instance = null;
		}

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
			_selection.RegisterValueChangedCallback(evt => OnSelectionChanged(evt.newValue));

			header.Add(_selection);
			AssetSelectionField.Attach(
				_selection,
				() => _source.Candidates(),
				null,
				candidate => _source.Describe(candidate)
			);

			_root.Add(header);

			// --- Scrollable body -----------------------------------------------------------
			var scroll = new ScrollView { style = { flexGrow = 1 } };
			// Lets the content fill the viewport, so the state boxes can be centred vertically.
			scroll.AddToClassList("scroll-full");

			_body = new VisualElement { style = { flexGrow = 1 } };
			scroll.Add(_body);
			_root.Add(scroll);

			_notLogged       = BuildStateBox("Not Logged In", "You need to be logged in to publish.");
			_noSelection     = BuildStateBox($"No {_source.Label} Selected", $"Please select a {_source.Label.ToLowerInvariant()} to continue.");
			_attachSection   = BuildAttachSection();
			_attachedSection = BuildAttachedSection();

			_body.Add(_notLogged);
			_body.Add(_noSelection);
			_body.Add(_attachSection);
			_body.Add(_attachedSection);

			// --- Footer --------------------------------------------------------------------
			var footer = new GroupBox { style = { flexDirection = FlexDirection.Row, marginLeft = 0, marginRight = 0, marginBottom = 0 } };
			footer.AddToClassList("p-8");
			footer.AddToClassList("border-t");

			_platforms = new PlatformPopupField { style = { flexGrow = 1 } };
			footer.Add(_platforms);

			_publish = new Button(() => PublishAsync().Forget()) { text = "Publish", style = { minWidth = 134 } };
			_publish.AddToClassList("large");
			footer.Add(_publish);

			_root.Add(footer);

			// --- Progress / result ---------------------------------------------------------
			_progressContainer = new VisualElement { style = { display = DisplayStyle.None } };
			_progressContainer.AddToClassList("p-8");

			_status   = new Label("Preparing...");
			_progress = new ProgressBar { value = 0 };
			_progress.AddToClassList("large");

			_progressContainer.Add(_status);
			_progressContainer.Add(_progress);
			_root.Add(_progressContainer);

			_result = new Label { style = { display = DisplayStyle.None, whiteSpace = WhiteSpace.Normal } };
			_result.AddToClassList("p-8");
			_root.Add(_result);

			// --- Initial state -------------------------------------------------------------
			// The user can log in (or out) while the panel is open: follow the event bus and the editor loop
			// (the event may fire before this panel subscribes).
			_userSubscription = Editor.CoreAPI?.EventAPI.Subscribe("user_updated", _ => OnUserUpdated());
			_lastUser         = CurrentUser?.Identifier.ToString() ?? string.Empty;
			EditorApplication.update += OnEditorUpdate;

			RefreshLogin();
			RefreshSelection();

			// Current is only filled after a fetch: ask the node so the panel does not claim
			// « Not Logged In » while a session is already open. It then attaches the asset of the
			// selection, which is what loads the images and the release — no request issued here, so
			// nothing can start before the asset itself is known.
			EnsureUserAsync().Forget();

			return _root;
		}

		// ---------------------------------------------------------------------------------------
		// Construction helpers (styled with the shared nox.style.uss classes)
		// ---------------------------------------------------------------------------------------

		private static VisualElement BuildStateBox(string title, string detail) {
			var box = new GroupBox { style = { flexGrow = 1, justifyContent = Justify.Center, display = DisplayStyle.None } };
			box.AddToClassList("p-8");
			box.AddToClassList("m-0");

			var titleLabel = new Label(title);
			titleLabel.AddToClassList("text-lg");
			titleLabel.AddToClassList("text-center");
			titleLabel.AddToClassList("mb-8");

			var detailLabel = new Label(detail);
			detailLabel.AddToClassList("text-center");

			box.Add(titleLabel);
			box.Add(detailLabel);
			return box;
		}

		private VisualElement BuildAttachSection() {
			var box = new GroupBox { style = { display = DisplayStyle.None } };
			box.AddToClassList("p-8");
			box.AddToClassList("m-0");
			box.AddToClassList("border-b");

			var title = new Label($"Attach {_source.Label}");
			title.AddToClassList("text-lg");
			title.AddToClassList("mb-8");
			box.Add(title);

			_attachId = AddRow(box, "Id", new TextField { }, "(auto-generate)") as TextField;
			_attachServer = AddRow(box, "Server", new TextField { }, "(auto-filled)") as TextField;

			if (_attachServer != null)
				_attachServer.SetEnabled(false);

			var attach = new Button(() => AttachAsync().Forget()) { text = "Attach" };
			attach.AddToClassList("large");
			attach.AddToClassList("mt-8");
			box.Add(attach);

			return box;
		}

		private VisualElement BuildAttachedSection() {
			// The current selection is a section of its own: the header above it already draws the top
			// rule, so only the bottom one is needed here. The last foldout drops its own rule so it is
			// not doubled.
			var container = new VisualElement { style = { display = DisplayStyle.None } };
			container.AddToClassList("border-b");

			// --- Information -----------------------------------------------------------------
			var info = new VisualElement();

			_infoServer = AddReadOnlyRow(info, "Server");
			_infoId     = AddReadOnlyRow(info, "Id");

			_infoName = AddRow(info, "Name", new TextField { }, null) as TextField;
			_infoDescription = AddRow(info, "Description", new TextField { multiline = true }, null) as TextField;

			// Values the mod contributes (capacity, viseme count…): typed rows, above the buttons.
			foreach (var entry in _source.Info)
				if (entry != null)
					info.Add(entry.Build());

			var refresh = new Button(() => _source.RefreshAsync().Forget()) { text = "Refresh" };
			var detach  = new Button(() => { _source.Detach(); RefreshSelection(); }) { text = "Detach" };
			var update  = new Button(() => UpdateInfoAsync().Forget()) { text = "Update" };

			var actions = new VisualElement { style = { flexDirection = FlexDirection.Row } };
			actions.AddToClassList("mt-8");
			foreach (var button in new[] { refresh, detach, update }) {
				button.AddToClassList("large");
				button.style.flexGrow = 1;
				button.style.marginRight = 4;
				actions.Add(button);
			}
			info.Add(actions);

			container.Add(Foldout("information", $"{_source.Label} information", info));

			// --- Release ---------------------------------------------------------------------
			var release = new VisualElement();

			var versionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
			versionRow.AddToClassList("mb-8");
			versionRow.Add(new Label("Version") { style = { minWidth = 100 } });

			_version = new UnsignedIntegerField { value = _source.Version, style = { flexGrow = 1, marginRight = 4 } };
			_version.AddToClassList("large");
			_version.RegisterValueChangedCallback(evt => {
				_source.Version = (ushort)evt.newValue;
				_releaseCheckedFor = null;
				RefreshSaveNoteAsync().Forget();
			});
			versionRow.Add(_version);

			var detect = new Button(() => DetectVersionAsync().Forget()) { text = "Detect", style = { minWidth = 80 } };
			detect.AddToClassList("large");
			versionRow.Add(detect);
			release.Add(versionRow);

			AddToggleRow(release, "Auto increment version", "auto_version");
			AddToggleRow(release, "Strict version checking", "strict_version");

			// Changelog of the release, laid out like the Description field of the information block:
			// it is stored on the release when it is created (publish), or patched by « Save ».
			_releaseNote = AddRow(release, "Changelog", new TextField { multiline = true, value = _source.ReleaseNote }, null) as TextField;
			_releaseNote.style.minHeight = 80;
			_releaseNote.RegisterValueChangedCallback(evt => _source.ReleaseNote = evt.newValue);

			var noteActions = new VisualElement { style = { flexDirection = FlexDirection.Row } };
			noteActions.AddToClassList("mt-8");

			// Saving only makes sense when the release of that version already exists.
			_saveNote = new Button(() => SaveReleaseNoteAsync().Forget()) { text = "Save" };
			_saveNote.AddToClassList("large");
			_saveNote.style.flexGrow = 1;
			_saveNote.SetEnabled(false);
			noteActions.Add(_saveNote);

			var loadReleases = new Button(() => LoadReleasesAsync().Forget()) { text = "Published releases" };
			loadReleases.AddToClassList("large");
			loadReleases.style.flexGrow = 1;
			loadReleases.style.marginLeft = 4;
			noteActions.Add(loadReleases);

			release.Add(noteActions);

			// Releases already uploaded: one row per version, with its status and file count.
			_releases = new VisualElement { style = { marginTop = 8 } };
			release.Add(_releases);

			container.Add(Foldout("release", "Release", release));

			// --- Images ----------------------------------------------------------------------
			_images = new AssetImageGrid();
			container.Add(Foldout("images", "Images", _images));

			// --- Sections contributed by the mod ---------------------------------------------
			foreach (var section in _source.Sections) {
				var content = new VisualElement();
				section.Build(content, _source);
				container.Add(Foldout(section.Id, section.Title, content, section.DefaultExpanded));
			}

			// The last section must not draw its own bottom rule: the container already has one.
			container.Children().LastOrDefault()?.AddToClassList("border-none");

			return container;
		}

		private VisualElement Foldout(string id, string title, VisualElement content, bool defaultExpanded = true) {
			var foldout = new AssetPublishFoldout(_source.Panel, id, title, defaultExpanded);
			foldout.Add(content);
			return foldout;
		}

		private static VisualElement AddRow(VisualElement parent, string label, VisualElement field, string placeholder) {
			var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
			row.Add(new Label(label) { style = { minWidth = 100 } });

			if (field is TextField text && placeholder != null)
				text.textEdition.placeholder = placeholder;

			field.AddToClassList("large");
			field.style.flexGrow = 1;

			row.Add(field);
			parent.Add(row);
			return field;
		}

		/// <summary>
		/// Row of a value the user cannot change (server, id…): a read-only field, so it lines up with the
		/// editable rows instead of looking like a label.
		/// </summary>
		private static TextField AddReadOnlyRow(VisualElement parent, string label) {
			var field = new TextField { isReadOnly = true };
			field.AddToClassList("large");
			return AddRow(parent, label, field, null) as TextField;
		}

		private void AddToggleRow(VisualElement parent, string label, string key) {
			var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 8 } };
			row.Add(new Label(label) { style = { minWidth = 150 } });

			var toggle = new Toggle { value = AssetPublishSettings.Get(_source.Panel, key, true) };
			toggle.AddToClassList("large");
			toggle.style.flexGrow = 1;
			toggle.RegisterValueChangedCallback(evt => AssetPublishSettings.Set(_source.Panel, key, evt.newValue));

			row.Add(toggle);
			parent.Add(row);
		}

		// ---------------------------------------------------------------------------------------
		// State
		// ---------------------------------------------------------------------------------------

		private static IUserAPI UserAPI
			=> Editor.CoreAPI?.ModAPI.GetMod("users")?.GetInstance<IUserAPI>();

		/// <summary>Current user, or <c>null</c> when nobody is logged in.</summary>
		private static ICurrentUser CurrentUser {
			get {
				try {
					return UserAPI?.Current;
				} catch {
					return null;
				}
			}
		}

		/// <summary>A user counts as logged in once the node has given it a server.</summary>
		private static bool LoggedIn {
			get {
				var user = CurrentUser;
				return user != null && !string.IsNullOrEmpty(user.Server);
			}
		}

		/// <summary>
		/// Reads the current user from the node the first time the panel needs it: <c>Current</c> is
		/// only filled after a <c>FetchCurrent</c>, so without this the panel says « Not Logged In » even
		/// though a session is open.
		/// </summary>
		private async UniTask EnsureUserAsync() {
			if (CurrentUser != null || _fetchingUser)
				return;

			var userApi = UserAPI;

			if (userApi == null)
				return;

			_fetchingUser = true;

			try {
				await userApi.FetchCurrent();
			} catch (Exception exception) {
				Logger.LogDebug($"Could not read the current user: {exception.Message}", tag: LogTag);
			} finally {
				_fetchingUser = false;
			}

			RefreshLogin();
			RefreshSelection();
		}

		private void OnUserUpdated() {
			// A new user means a new server: the previously attached asset and the auto-attach attempt
			// (which depends on the user) are stale.
			_source.ResetAttached();
			_imagesBoundFor = null;
			_autoAttachTried = null;

			RefreshLogin();
			RefreshSelection();
		}

		private void RefreshLogin() {
			var user     = CurrentUser;
			var loggedIn = LoggedIn;

			_notLogged.style.display = loggedIn ? DisplayStyle.None : DisplayStyle.Flex;

			if (loggedIn && _attachServer != null)
				_attachServer.SetValueWithoutNotify(user.Server);

			if (!loggedIn) {
				_noSelection.style.display     = DisplayStyle.None;
				_attachSection.style.display   = DisplayStyle.None;
				_attachedSection.style.display = DisplayStyle.None;
			}
			
		}

		/// <summary>
		/// Watches the current user from the editor loop: the users mod fills <c>Current</c> asynchronously
		/// (and <c>user_updated</c> may fire before the panel subscribes), so the form assumes nothing and
		/// simply reacts when the identifier changes.
		/// </summary>
		private void OnEditorUpdate() {
			var user       = CurrentUser;
			var identifier = user?.Identifier.ToString() ?? string.Empty;

			if (identifier != _lastUser) {
				_lastUser = identifier;
				OnUserUpdated();
				return;
			}

			// Nobody known yet: retry the fetch, at most every 5 seconds.
			if (user == null && !_fetchingUser && EditorApplication.timeSinceStartup >= _nextUserFetch) {
				_nextUserFetch = EditorApplication.timeSinceStartup + 5d;
				EnsureUserAsync().Forget();
			}
		}

		private void OnSelectionChanged(UnityEngine.Object selection) {
			_source.Selection = selection;

			// Another descriptor: the asset attached to the previous one is irrelevant.
			_source.ResetAttached();
			_autoAttachTried = null;

			RefreshSelection();
		}

		private void RefreshSelection() {
			if (_refreshing)
				return;

			_refreshing = true;

			bool autoAttach;

			try {
				autoAttach = RefreshSelectionInternal();
			} finally {
				_refreshing = false;
			}

			// Started outside the guard: the attach completes synchronously often enough, and its
			// AttachedChanged handler must be able to refresh the form.
			if (autoAttach)
				AutoAttachAsync().Forget();
		}

		private bool RefreshSelectionInternal() {
			var selection = _source.Selection;
			var attached  = _source.Attached;

			_selection.SetValueWithoutNotify(selection);

			var loggedIn = LoggedIn;

			// Only one state box at a time: not logged in > nothing selected > attach form > attached.
			_notLogged.style.display = loggedIn ? DisplayStyle.None : DisplayStyle.Flex;

			// Same states as shared notifications, so the other panels show what this one shows.
			AssetPublisherNotifications.Update(_source, loggedIn);

			_noSelection.style.display = !loggedIn || selection != null
				? DisplayStyle.None
				: DisplayStyle.Flex;

			_attachSection.style.display   = loggedIn && selection != null && attached == null ? DisplayStyle.Flex : DisplayStyle.None;
			_attachedSection.style.display = loggedIn && attached != null ? DisplayStyle.Flex : DisplayStyle.None;

			_platforms?.SetEnabled(selection != null);
			_publish?.SetEnabled(attached != null && AssetNotificationHelper.Allowed);

			// A published descriptor already carries its asset id: attach it without asking.
			var autoAttach = loggedIn && selection != null && attached == null && !ReferenceEquals(_autoAttachTried, selection);

			if (autoAttach)
				_autoAttachTried = selection;

			if (attached != null)
				RefreshAttached();

			return autoAttach;
		}

		private async UniTask AutoAttachAsync() {
			var attached = await _source.TryAutoAttachAsync();

			// SetAttached already raised AttachedChanged (→ RefreshSelection); only refresh when nothing
			// was attached, so the attach form/state stays correct.
			if (!attached)
				RefreshSelection();
		}

		private void RefreshAttached() {
			var attached = _source.Attached;

			_infoServer.SetValueWithoutNotify(attached?.Server ?? "-");
			_infoId.SetValueWithoutNotify(attached?.Id.ToString() ?? "-");
			_infoName.SetValueWithoutNotify(_source.AttachedTitle ?? string.Empty);
			_infoDescription.SetValueWithoutNotify(_source.AttachedDescription ?? string.Empty);

			if (_version != null)
				_version.SetValueWithoutNotify(_source.Version);

			if (_releaseNote != null)
				_releaseNote.SetValueWithoutNotify(_source.ReleaseNote ?? string.Empty);

			// A refresh replaces the asset instance but not its identity: compare server:id, so the grid
			// and the release check only run once per asset.
			var key = attached == null ? null : $"{attached.Server}:{attached.Id}";

			if (attached != null && _images != null && key != _imagesBoundFor) {
				_imagesBoundFor = key;
				_images.Bind(attached.Server, _source.Endpoint, attached.Id.ToString());
				// The asset was just fetched to attach it: show those images instead of fetching it again.
				_images.ShowAsync(attached).Forget();
			}

			RefreshSaveNoteAsync().Forget();
		}

		// ---------------------------------------------------------------------------------------
		// Actions
		// ---------------------------------------------------------------------------------------

		private async UniTask AttachAsync() {
			if (uint.TryParse(_attachId?.value ?? string.Empty, out var id) == false)
				id = 0;

			var asset = await _source.AttachAsync(id);

			if (asset == null)
				Logger.OpenDialog("Error", $"Failed to attach the {_source.Label.ToLowerInvariant()}.", "Ok");

			RefreshSelection();
		}

		private async UniTask UpdateInfoAsync() {
			var updated = await _source.UpdateAsync(_infoName?.value ?? string.Empty, _infoDescription?.value ?? string.Empty);

			if (!updated)
				Logger.OpenDialog("Error", "Failed to update the information.", "Ok");
		}

		private async UniTask DetectVersionAsync() {
			var latest  = await AssetPublisher.DetectLatestVersion(_source.Server, _source.Endpoint, _source.AssetRef);
			var version = (ushort)(latest + 1);

			_source.Version = version;
			_version.SetValueWithoutNotify(version);
		}

		/// <summary>Applies the typed note to the release of the current version.</summary>
		private async UniTask SaveReleaseNoteAsync() {
			var saved = await _source.SaveReleaseNoteAsync(_releaseNote?.value ?? string.Empty);

			if (!saved) {
				Logger.OpenDialog(
					"Release",
					$"No release v{_source.Version} to update yet: the changelog is sent when you publish.",
					"Ok"
				);
			}

			RefreshSaveNoteAsync().Forget();
		}

		/// <summary>
		/// Enables « Save » only when the release of the current version exists: before that, the
		/// changelog is written by the publish itself.
		/// </summary>
		private async UniTask RefreshSaveNoteAsync() {
			if (_saveNote == null)
				return;

			// One check per (asset, version): the publisher refreshes itself several times per open.
			var key = $"{_source.Server}:{_source.AssetRef}:{_source.Version}";

			if (key == _releaseCheckedFor)
				return;

			_releaseCheckedFor = key;

			var release = await _source.FetchCurrentReleaseAsync();

			if (key == _releaseCheckedFor)
				_saveNote.SetEnabled(release != null);
		}

		/// <summary>Lists the releases the asset already carries, newest first.</summary>
		private async UniTask LoadReleasesAsync() {
			if (_releases == null)
				return;

			_releases.Clear();
			_releases.Add(new Label($"Reading releases of v{_source.Version}...") { style = { opacity = 0.7f } });

			var releases = await _source.FetchReleasesAsync(_source.Version);

			_releases.Clear();

			if (releases.Length == 0) {
				_releases.Add(new Label("No release published yet.") { style = { opacity = 0.7f } });
				return;
			}

			foreach (var release in releases) {
				var row = new VisualElement {
					style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 }
			};

				row.Add(new Label($"v{release.Name}") { style = { minWidth = 60 } });
				row.Add(new Label(release.Status?.Status.ToString() ?? "-") { style = { minWidth = 90, opacity = 0.7f } });
				row.Add(new Label($"{release.Files?.Length ?? 0} file(s)") { style = { flexGrow = 1, opacity = 0.7f } });

				var use = new Button(() => {
					if (ushort.TryParse(release.Name, out var version)) {
						_source.Version = version;
						_version.SetValueWithoutNotify(version);
					}

					var note = release.Changelog?.Resolve() ?? string.Empty;
					_source.ReleaseNote = note;
					_releaseNote.SetValueWithoutNotify(note);
				}) { text = "Use" };

				use.AddToClassList("large");
				row.Add(use);

				_releases.Add(row);
			}
		}

		private async UniTask PublishAsync() {
			if (_publishing)
				return;

			_publishing = true;
			_publish.SetEnabled(false);
			_result.style.display            = DisplayStyle.None;
			_progressContainer.style.display = DisplayStyle.Flex;
			_progress.value                  = 0;
			_status.text                     = "Preparing...";

			try {
				var result = await AssetPublisher.Publish(new AssetPublishRequest {
					Server            = _source.Server,
					Endpoint          = _source.Endpoint,
					Asset             = _source.AssetRef,
					Version           = _source.Version,
					ReleaseNote       = _source.ReleaseNote,
					Engine            = EngineExtensions.CurrentEngine,
					Platforms         = _platforms.value,
					Builder           = _source.Builder,
					Constraints       = _source.Constraints,
					State             = _source.State,
					AutoVersion       = AssetPublishSettings.Get(_source.Panel, "auto_version", true),
					StrictVersion     = AssetPublishSettings.Get(_source.Panel, "strict_version", true),
					Progress          = (progress, message) => {
						_progress.value = Mathf.Clamp01(progress) * 100f;
						_status.text    = message;
					},
					Status            = message => {
						if (!string.IsNullOrEmpty(message))
							_status.text = message;
					},
					OnVersionResolved = version => {
						_source.Version = version;
						_source.OnVersionResolved(version);
						_version.SetValueWithoutNotify(version);
					}
				});

				_status.text    = result.Message;
				_progress.value = result.Success ? 100 : 0;

				_result.text          = result.Message;
				_result.style.color   = result.Success ? new Color(0.4f, 0.9f, 0.4f) : new Color(0.95f, 0.4f, 0.4f);
				_result.style.display = DisplayStyle.Flex;

				if (result.Success) {
					_source.OnPublished(result);
					await _source.RefreshAsync();
				}
			} catch (Exception ex) {
				Logger.LogError(new Exception("Publish failed", ex), tag: LogTag);
				_status.text = $"Publish failed: {ex.Message}";
			} finally {
				_publishing = false;
				_publish.SetEnabled(_source.Attached != null && AssetNotificationHelper.Allowed);
			}
		}
	}
}
