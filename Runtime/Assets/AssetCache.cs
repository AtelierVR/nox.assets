using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Runtime.Assets {
	/// <summary>
	/// Content-addressed cache shared by every asset type. Entries are stored under the
	/// filesystem-safe form of their hash (<c>&lt;type&gt;-&lt;hex&gt;</c>), so the same bundle
	/// downloaded for two releases or two asset types is only fetched once. Concurrent downloads
	/// of the same URL/hash share one transfer.
	/// </summary>
	public class AssetCache : IAssetCache, IDisposable {
		public const string LogTag = "Assets";

		private readonly string              _root;
		private readonly List<AssetDownload> _active = new();
		private readonly object              _lock   = new();

		public AssetCache(string root = null) {
			_root = string.IsNullOrEmpty(root)
				? Path.Combine(Constants.CachePath, "assets")
				: root;

			try {
				Directory.CreateDirectory(_root);
			} catch (Exception ex) {
				Logger.LogError(ex, tag: LogTag);
			}
		}

		public string Root
			=> _root;

		public string PathFor(IAssetHash hash) {
			var name = AssetHash.ToFileName(hash);
			return string.IsNullOrEmpty(name) ? null : Path.Combine(_root, name);
		}

		public bool Has(IAssetHash hash) {
			var path = PathFor(hash);
			return !string.IsNullOrEmpty(path) && File.Exists(path);
		}

		public IAssetHash[] Hashes
			=> Directory.Exists(_root)
				? Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly)
					.Where(file => !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
					.Select(Path.GetFileName)
					.Select(AssetHash.Parse)
					.Where(AssetHash.Has)
					.ToArray()
				: Array.Empty<IAssetHash>();

		public void Clear() {
			CancelAll();

			try {
				if (Directory.Exists(_root))
					Directory.Delete(_root, true);
			} catch (Exception ex) {
				Logger.LogError(ex, tag: LogTag);
			}
		}

		public void Clear(IAssetHash hash) {
			var path = PathFor(hash);

			try {
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
					File.Delete(path);
			} catch (Exception ex) {
				Logger.LogError(ex, tag: LogTag);
			}
		}

		public async UniTask<string> Store(IAssetHash hash, string sourcePath) {
			var path = PathFor(hash);

			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) {
				Logger.LogWarning($"Cannot store '{sourcePath}' under '{hash?.Value}'.", tag: LogTag);
				return null;
			}

			try {
				await UniTask.RunOnThreadPool(() => {
					if (File.Exists(path))
						File.Delete(path);
					File.Copy(sourcePath, path);
				});
			} catch (Exception ex) {
				Logger.LogError(ex, tag: LogTag);
				return null;
			}

			return path;
		}

		public IAssetDownload Download(
			string url,
			IAssetHash hash = null,
			IReadOnlyDictionary<string, string> headers = null,
			Action<float, ulong> progress = null,
			CancellationToken token = default
		) {
			lock (_lock) {
				var running = _active.Find(candidate =>
					candidate.Url == url
					|| (AssetHash.Has(hash) && AssetHash.Matches(candidate.Hash, hash)));

				if (running != null) {
					running.Subscribe(progress);
					return running;
				}
			}

			return new AssetDownload(this, url, hash, headers, progress, token);
		}

		internal void Track(AssetDownload download) {
			lock (_lock)
				_active.Add(download);
		}

		internal void Untrack(AssetDownload download) {
			lock (_lock)
				_active.Remove(download);
		}

		private void CancelAll() {
			lock (_lock) {
				foreach (var download in _active.ToArray())
					download.Cancel();
				_active.Clear();
			}
		}

		public void Dispose()
			=> CancelAll();
	}
}
