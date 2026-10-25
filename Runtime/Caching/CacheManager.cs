using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Network.Assets;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Runtime.Caching {
	/// <summary>
	/// The shared asset cache: hash-addressed files under <c>&lt;cache&gt;/assets</c>, one
	/// <see cref="Cache"/> handle per in-flight URL/hash, and cache events for the UI.
	/// <para>
	/// Files downloaded by the former per-mod caches (<c>avatars/</c>, <c>worlds/</c>) are migrated
	/// on first lookup, so an upgrade does not re-download everything.
	/// </para>
	/// </summary>
	public sealed class CacheManager : IDisposable {
		public const string LogTag = "AssetsCache";

		/// <summary>Per-mod directories used before the cache was shared.</summary>
		private static readonly string[] LegacyDirectories = { "avatars", "worlds" };

		private readonly FileSystemWatcher _watcher;
		internal readonly List<Cache>      Caching = new();

		public CacheManager() {
			_watcher = new FileSystemWatcher(Root) {
				NotifyFilter          = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
				EnableRaisingEvents   = true,
				IncludeSubdirectories = false
			};
			_watcher.Changed += OnFile;
			_watcher.Created += OnFile;
			_watcher.Deleted += OnFile;
			_watcher.Renamed += OnRenameFile;
		}

		/// <summary>Absolute path of the shared cache directory.</summary>
		public static string Root {
			get {
				var dir = Path.Combine(Nox.CCK.Utils.Constants.CachePath, "assets");
				if (!Directory.Exists(dir))
					Directory.CreateDirectory(dir);
				return dir;
			}
		}

		private void OnFile(object sender, FileSystemEventArgs e)
			=> OnFileAsync(e).Forget();

		private async UniTask OnFileAsync(FileSystemEventArgs e) {
			await UniTask.SwitchToMainThread();
			var path = Path.GetRelativePath(Root, e.FullPath);

			if (e.ChangeType == WatcherChangeTypes.Created)
				Emit("asset_cache_added", path);
			else if (e.ChangeType == WatcherChangeTypes.Deleted)
				Emit("asset_cache_removed", path);
		}

		private void OnRenameFile(object sender, RenamedEventArgs e)
			=> OnRenameFileAsync(e).Forget();

		private async UniTask OnRenameFileAsync(RenamedEventArgs e) {
			await UniTask.SwitchToMainThread();
			var oldPath = Path.GetRelativePath(Root, e.OldFullPath);
			var newPath = Path.GetRelativePath(Root, e.FullPath);

			Emit("asset_cache_removed", oldPath);
			Emit("asset_cache_added", newPath);
		}

		private static void Emit(string name, params object[] args)
			=> Nox.Assets.Runtime.Main.Instance?.CoreAPI?.EventAPI.Emit(name, args);

		/// <summary>Running download for a URL or hash, or <c>null</c>.</summary>
		public Cache GetDownload(string url, string hash)
			=> Caching.Find(c => hash != null && c.Hash == hash || c.Url == url);

		/// <summary>
		/// Returns the download of <paramref name="entry"/>, reusing the running one for the same URL
		/// or hash. The variant follows what the entry describes: a raw URL (<see cref="CacheUrl"/>),
		/// the bundle of an asset whose analysis can be followed (<see cref="CacheIdentifier"/>), or a
		/// bundle file alone (<see cref="CacheAsset"/>).
		/// </summary>
		public Cache AddDownload(ICacheEntry entry, CancellationToken token = default, UnityAction<float> progress = null) {
			if (entry == null || string.IsNullOrEmpty(entry.Url))
				return null;

			var existing = GetDownload(entry.Url, entry.Hash);

			if (existing == null)
				existing = entry.File == null
					? new CacheUrl(this, entry.Url, entry.Hash, token)
					: entry.Identifier.IsValid()
						? new CacheIdentifier(this, entry.Identifier, entry.File, token)
						: new CacheAsset(this, entry.File, token);

			existing.Subscribe(progress);
			return existing;
		}

		/// <summary>Absolute path a hash is (or would be) stored at.</summary>
		public string PathFor(string hash)
			=> string.IsNullOrEmpty(hash) ? null : Path.Combine(Root, hash);

		/// <summary>
		/// Path of a stored hash: the shared cache first, then the former per-mod directories
		/// (migrated on the spot). <c>null</c> when the hash is not stored.
		/// </summary>
		public string Existing(string hash) {
			if (string.IsNullOrEmpty(hash))
				return null;

			var path = PathFor(hash);

			if (File.Exists(path))
				return path;

			foreach (var legacy in LegacyDirectories) {
				var old = Path.Combine(Nox.CCK.Utils.Constants.CachePath, legacy, hash);

				if (!File.Exists(old))
					continue;

				try {
					File.Move(old, path);
					Logger.LogDebug($"Migrated '{legacy}/{hash}' into the shared asset cache.", tag: LogTag);
					return path;
				} catch (Exception e) {
					Logger.LogWarning($"Cannot migrate '{old}': {e.Message}", tag: LogTag);
					return old;
				}
			}

			return null;
		}

		public bool Has(string hash)
			=> Existing(hash) != null;

		public void Clear(string hash) {
			var path = Existing(hash);

			if (path != null)
				File.Delete(path);
		}

		public void Clear() {
			if (Directory.Exists(Root))
				Directory.Delete(Root, true);
		}

		/// <summary>Moves a verified download under its hash and returns the stored path.</summary>
		public string Save(string hash, string sourcePath) {
			var path = PathFor(hash);

			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sourcePath))
				return null;

			if (File.Exists(path))
				File.Delete(path);

			File.Copy(sourcePath, path, true);
			return path;
		}

		public void Dispose() {
			foreach (var caching in Caching)
				caching.Cancel();

			_watcher.Dispose();
			Caching.Clear();
		}
	}
}
