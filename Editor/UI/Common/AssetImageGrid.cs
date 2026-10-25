using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Nox.Network.Assets;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Images of the asset, as a grid of tiles: one per image plus a <c>+</c> tile that adds a new one.
	/// Clicking a tile opens a popup with its metadata (priority, alt text), a <c>Save</c> and a
	/// <c>Remove</c> button. Uploads report their real progress (bytes sent).
	/// <para>
	/// The tiles are drawn <b>before</b> the thumbnails are downloaded: the model returned by the node
	/// is applied immediately (upload, delete, reload) and the pictures fill in as they arrive, so the
	/// grid never freezes while an image is being fetched.
	/// </para>
	/// </summary>
	public class AssetImageGrid : VisualElement {
		private static readonly string[] Extensions = { "png", "jpg", "jpeg", "tga", "psd", "exr" };

		private const float MinTileSize = 96f;
		private const float TileGap     = 4f;

		private readonly VisualElement _grid;
		private readonly ProgressBar   _progress;
		private readonly Label         _status;

		private float _layoutWidth = -1f;

		private readonly List<IAssetImage> _images   = new();
		private readonly List<Image>       _previews = new();

		/// <summary>Thumbnails already downloaded, keyed by image url (they survive a reload).</summary>
		private readonly Dictionary<string, Texture2D> _thumbnails = new();

		private string        _server;
		private AssetEndpoint _endpoint;
		private string        _assetRef;

		public AssetImageGrid() {
			style.marginBottom = 6;

			_grid = new VisualElement {
				style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap }
			};

			// The tiles are sized from the available width: re-layout whenever it changes.
			_grid.RegisterCallback<GeometryChangedEvent>(evt => {
				if (Mathf.Abs(evt.newRect.width - _layoutWidth) < 0.5f)
					return;

				_layoutWidth = evt.newRect.width;
				Refresh();
			});

			_progress = new ProgressBar { value = 0, style = { display = DisplayStyle.None } };
			_progress.AddToClassList("large");
			_progress.AddToClassList("mb-8");

			_status = new Label { style = { marginBottom = 4, opacity = 0.7f, whiteSpace = WhiteSpace.Normal } };

			// Feedback sits above the tiles, so an upload does not push the grid down.
			Add(_status);
			Add(_progress);
			Add(_grid);

			Apply(Array.Empty<IAssetImage>());
		}

		/// <summary>Binds the grid to an asset of the pipeline.</summary>
		public void Bind(string server, AssetEndpoint endpoint, string assetRef) {
			_server   = server;
			_endpoint = endpoint;
			_assetRef = assetRef;
		}

		// --- Pipeline -------------------------------------------------------------------------

		/// <summary>Reads the asset and shows its images.</summary>
		public async UniTask ReloadAsync() {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(_assetRef))
				return;

			await ShowAsync(await assets.Fetch(_server, _endpoint, _assetRef));
		}

		/// <summary>
		/// Shows the images of an asset the caller already has (the publisher fetches it to attach it):
		/// no extra request, the tiles appear at once and the thumbnails follow.
		/// </summary>
		public async UniTask ShowAsync(IAsset asset) {
			Apply(asset);

			if (_images.Count == 0)
				SetStatus("No image yet.");

			await LoadThumbnailsAsync();
		}

		/// <summary>Uploads an image file: the new tile appears before its thumbnail.</summary>
		public async UniTask UploadAsync(string path) {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(_assetRef)) {
				SetStatus("Not attached to an asset.");
				return;
			}

			var name = Path.GetFileName(path);

			if (LoadTexture(path) == null) {
				SetStatus($"Cannot read {name}.");
				return;
			}

			ShowProgress(true);
			SetStatus($"Uploading {name}...");

			var asset = await assets.AddImage(
				_server,
				_endpoint,
				_assetRef,
				path,
				(ratio, bytes) => {
					_progress.value = Mathf.Clamp01(ratio) * 100f;
					SetStatus($"Uploading {name}... {ratio * 100f:F0}% ({bytes / 1_000_000f:F2} MB)");
				}
			);

			ShowProgress(false);

			if (asset == null) {
				SetStatus("Failed to upload the image.");
				return;
			}

			// The node returned the updated asset: draw the tiles now, download the pictures after.
			Apply(asset);
			SetStatus("Image uploaded.");

			await LoadThumbnailsAsync();
		}

		/// <summary>Deletes the image at <paramref name="index"/>; the tile disappears immediately.</summary>
		public async UniTask RemoveAsync(int index) {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(_assetRef))
				return;

			// Optimistic: drop the tile right away, the server confirms (or restores) afterwards.
			var previous = new List<IAssetImage>(_images);

			RemoveLocally(index);
			SetStatus("Removing...");

			var asset = await assets.RemoveImage(_server, _endpoint, _assetRef, index);

			if (asset == null) {
				Apply(previous);
				SetStatus("Failed to remove the image.");
				return;
			}

			Apply(asset);
			SetStatus("Image removed.");

			await LoadThumbnailsAsync();
		}

		/// <summary>Updates the priority and alt text of the image at <paramref name="index"/>.</summary>
		public async UniTask SaveAsync(int index, int priority, string alt) {
			var assets = Editor.Pipeline;

			if (assets == null || string.IsNullOrEmpty(_assetRef))
				return;

			SetStatus("Saving...");

			var asset = await assets.UpdateImage(_server, _endpoint, _assetRef, index, priority, alt);

			if (asset == null) {
				SetStatus("Failed to save the image.");
				return;
			}

			Apply(asset);
			SetStatus("Image saved.");
		}

		// --- Model → UI -----------------------------------------------------------------------

		/// <summary>Draws the images carried by <paramref name="asset"/> (null clears the grid).</summary>
		private void Apply(IAsset asset)
			=> Apply(asset?.Images);

		/// <summary>
		/// Draws the given images at once (thumbnails come from the cache, or stay empty until
		/// <see cref="LoadThumbnailsAsync"/> fills them).
		/// </summary>
		private void Apply(IEnumerable<IAssetImage> images) {
			_images.Clear();

			foreach (var image in images ?? Array.Empty<IAssetImage>())
				if (image != null)
					_images.Add(image);

			Refresh();
		}

		private void RemoveLocally(int index) {
			if (index < 0 || index >= _images.Count)
				return;

			var remaining = new List<IAssetImage>(_images);
			remaining.RemoveAt(index);
			Apply(remaining);
		}

		/// <summary>Downloads the missing thumbnails and fills the tiles as they arrive.</summary>
		private async UniTask LoadThumbnailsAsync() {
			var assets = Editor.Pipeline;

			if (assets == null)
				return;

			for (var i = 0; i < _images.Count; i++) {
				var url = _images[i]?.Url;

				if (string.IsNullOrEmpty(url))
					continue;

				if (!_thumbnails.TryGetValue(url, out var texture)) {
					texture = LoadTexture(await assets.DownloadUrl(url));

					if (texture == null)
						continue;

					_thumbnails[url] = texture;
				}

				// The list may have changed while the download ran: only fill the matching tile.
				if (i < _images.Count && i < _previews.Count && _images[i]?.Url == url)
					_previews[i].image = texture;
			}
		}

		private void Refresh() {
			_grid.Clear();
			_previews.Clear();

			var size = TileSize();

			for (var i = 0; i < _images.Count; i++)
				_grid.Add(CreateTile(i, _images[i], size));

			_grid.Add(CreateAddTile(size));
		}

		/// <summary>
		/// Edge of a tile: enough columns of at least <see cref="MinTileSize"/> to fill the available
		/// width, each one stretched to take the remaining space. Every tile also carries a trailing
		/// <see cref="TileGap"/>, so a full row is <c>columns * (size + gap)</c> wide.
		/// </summary>
		private float TileSize() {
			var available = _layoutWidth;

			// Before the first geometry event (or when the panel is rebuilt), fall back to the measured
			// width of the container.
			if (float.IsNaN(available) || available <= 0f)
				available = _grid.resolvedStyle.width;

			if (float.IsNaN(available) || available <= 0f)
				available = MinTileSize * 4f;

			var columns = Mathf.Max(1, Mathf.FloorToInt((available + TileGap) / (MinTileSize + TileGap)));
			var size    = Mathf.Floor((available - columns * TileGap) / columns);

			return Mathf.Max(MinTileSize, size);
		}

		private VisualElement CreateTile(int index, IAssetImage image, float size) {
			var tile = new VisualElement {
				style = {
					width       = size,
					minWidth    = size,
					maxWidth    = size,
					height      = size,
					marginRight = TileGap,
					marginBottom = TileGap
				}
			};
			StyleImageTile(tile);

			if (!string.IsNullOrEmpty(image.Alt))
				tile.tooltip = image.Alt;

			var url = image.Url;
			var preview = new Image {
				image     = !string.IsNullOrEmpty(url) && _thumbnails.TryGetValue(url, out var cached) ? cached : null,
				scaleMode = ScaleMode.ScaleToFit,
				style     = { flexGrow = 1, marginLeft = 2, marginRight = 2, marginTop = 2, marginBottom = 2 }
			};

			_previews.Add(preview);
			tile.Add(preview);

			tile.RegisterCallback<ClickEvent>(_ => OpenPopup(index, image, tile.worldBound));

			return tile;
		}

		private VisualElement CreateAddTile(float size) {
			// Plain button: the editor stylesheet draws its background and its hover state, so the tile
			// must not override them. min/max are pinned so the USS defaults do not resize it.
			var tile = new Button(AskForPath) {
				text  = "+",
				style = {
					width        = size,
					minWidth     = size,
					maxWidth     = size,
					height       = size,
					marginLeft   = 0,
					marginRight  = TileGap,
					marginBottom = TileGap,
					fontSize     = 28
				}
			};

			tile.tooltip = "Add an image";

			return tile;
		}

		/// <summary>
		/// Look of an image tile: rounded corners and a darker panel than the window (variables of
		/// <c>nox.style.uss</c>). The <c>+</c> button keeps the native button style, so it keeps its hover.
		/// </summary>
		private static void StyleImageTile(VisualElement tile) {
			tile.AddToClassList("bg-secondary");
			tile.AddToClassList("rounded-sm");
		}

		private void OpenPopup(int index, IAssetImage image, Rect anchor) {
			var tileIndex = index;

			UnityEditor.PopupWindow.Show(anchor, new ImagePopup(
				image,
				_url => !string.IsNullOrEmpty(_url) && _thumbnails.TryGetValue(_url, out var thumbnail) ? thumbnail : null,
				(priority, alt) => SaveAsync(tileIndex, priority, alt).Forget(),
				() => RemoveAsync(tileIndex).Forget()
			));
		}

		private void AskForPath() {
			var path = EditorUtility.OpenFilePanel("Select an image", string.Empty, string.Join(",", Extensions));

			if (!string.IsNullOrEmpty(path))
				UploadAsync(path).Forget();
		}

		private void ShowProgress(bool visible)
			=> _progress.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

		/// <summary>One-line feedback under the grid.</summary>
		public void SetStatus(string status)
			=> _status.text = status;

		/// <summary>Reads an image file into a texture (the cache stores raw bytes, without extension).</summary>
		public static Texture2D LoadTexture(string path) {
			try {
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
					return null;

				var texture = new Texture2D(2, 2);

				return texture.LoadImage(File.ReadAllBytes(path)) ? texture : null;
			} catch (Exception) {
				return null;
			}
		}

		/// <summary>Popup opened by a tile: priority + alt text, Save and Remove.</summary>
		private sealed class ImagePopup : PopupWindowContent {
			private const float PopupWidth = 320f;

			private readonly IAssetImage _image;
			private readonly Func<string, Texture2D> _thumbnail;
			private readonly Action<int, string> _save;
			private readonly Action _remove;

			public ImagePopup(
				IAssetImage image,
				Func<string, Texture2D> thumbnail,
				Action<int, string> save,
				Action remove
			) {
				_image     = image;
				_thumbnail = thumbnail;
				_save      = save;
				_remove    = remove;
			}

			public override Vector2 GetWindowSize()
				=> new(PopupWidth, 380f);

			public override void OnGUI(Rect rect) { }

			public override VisualElement CreateGUI() {
				var root = new VisualElement();
				root.AddToClassList("p-8");
				// PopupWindowContent sizes itself from the content: without an explicit width the root
				// stretches to the whole editor window.
				root.style.width = PopupWidth;

				var preview = new Image {
					image     = _thumbnail?.Invoke(_image?.Url),
					scaleMode = ScaleMode.ScaleToFit,
					style     = { height = 160, alignSelf = Align.Center, marginBottom = 8 }
				};
				root.Add(preview);

				var priority = new IntegerField("Priority") { value = _image?.Priority ?? 0 };
				priority.AddToClassList("large");
				priority.tooltip = "Higher priority images are picked first.";
				priority.style.marginBottom = 8;
				root.Add(priority);

				var alt = new TextField("Alt") { value = _image?.Alt ?? string.Empty };
				alt.AddToClassList("large");
				alt.style.marginBottom = 8;
				root.Add(alt);

				var actions = new VisualElement { style = { flexDirection = FlexDirection.Row } };
				actions.AddToClassList("mt-8");

				var save = new Button(() => {
					_save?.Invoke(priority.value, alt.value);
					editorWindow?.Close();
				}) { text = "Save", style = { flexGrow = 1, marginRight = 4 } };
				save.AddToClassList("large");
				actions.Add(save);

				var remove = new Button(() => {
					_remove?.Invoke();
					editorWindow?.Close();
				}) { text = "Remove", style = { flexGrow = 1 } };
				remove.AddToClassList("large");
				actions.Add(remove);

				root.Add(actions);
				return root;
			}
		}
	}
}
