using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Runtime.Caching {
	/// <summary>
	/// Base of a cache-backed download: progress, cancellation, de-duplication and the save into the
	/// shared asset cache.
	/// <para>
	/// The variants only describe <b>what</b> is downloaded and how a pending analysis is handled:
	/// <see cref="CacheUrl"/> (raw URL), <see cref="CacheIdentifier"/> (bundle of an asset, whose
	/// analysis is waited for) and <see cref="CacheAsset"/> (bundle file alone, downloaded as-is).
	/// </para>
	/// </summary>
	public abstract class Cache : ICaching {
		public const string LogTag = CacheManager.LogTag;

		private readonly CacheManager            _cache;
		private readonly CancellationToken       _externalToken;
		private          CancellationTokenSource _cts;
		private          float                   _progress;

		protected Cache(CacheManager cache, CancellationToken token = default) {
			_cache         = cache;
			_externalToken = token;
			_progress      = 0f;
		}

		/// <summary>URL of the file, or <c>null</c> while it is not known yet.</summary>
		public abstract string Url { get; }

		/// <summary>Expected hash (the cache file name), or <c>null</c> while it is not known yet.</summary>
		public abstract string Hash { get; }

		/// <inheritdoc />
		public UnityEvent<float> OnProgress { get; } = new();

		/// <summary>Adds a progress listener.</summary>
		public void Subscribe(UnityAction<float> progress) {
			if (progress != null)
				OnProgress.AddListener(progress);
		}

		private void SetProgress(float value, ulong size = 0) {
			_progress = value;
			SendEvent();
			OnProgress.Invoke(_progress);
		}

		private void SendEvent()
			=> Main.Instance.CoreAPI
				.EventAPI
				.Emit("asset_cache_download", Url, Hash, IsRunning, Progress);

		/// <inheritdoc />
		public bool IsRunning
			=> _cts is { IsCancellationRequested: false };

		/// <inheritdoc />
		public float Progress
			=> _progress;

		/// <inheritdoc />
		public void Cancel() {
			if (_cts is not { IsCancellationRequested: false }) {
				Logger.Log("Cancellation requested but not running.", tag: LogTag);
				return;
			}

			_cts.Cancel();
			_cts.Dispose();
			_cts = null;
			SendEvent();
		}

		/// <inheritdoc />
		public async UniTask Wait() {
			if (!IsRunning) return;
			Logger.LogDebug($"Waiting for download to complete: {Url} (Hash: {Hash})", tag: LogTag);
			await UniTask.WaitUntil(() => !IsRunning, cancellationToken: _externalToken);
		}

		/// <inheritdoc />
		public async UniTask Start() {
			if (IsRunning) {
				Logger.LogDebug("Already running.", tag: LogTag);
				return;
			}

			_cts = new CancellationTokenSource();

			// Combine internal and external tokens
			var combinedToken = _cts.Token;
			if (_externalToken != default) {
				var combined = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _externalToken);
				combinedToken = combined.Token;
			}

			_cache.Caching.Add(this);
			Logger.LogDebug($"Starting download for {Url} (Hash: {Hash})", tag: LogTag);

			try {
				SetProgress(0f);

				// Download
				var result = await Download(combinedToken);

				if (combinedToken.IsCancellationRequested) {
					SetProgress(0f);
					throw new OperationCanceledException("Download was cancelled.");
				}

				if (result is not { Success: true }) {
					SetProgress(0f);
					throw new Exception($"Download failed: {result?.Error ?? "No data received."}");
				}

				// Save to cache
				Logger.LogDebug($"Download completed for {Url} (Hash: {Hash}). Saving to cache...", tag: LogTag);
				Main.Instance.Cache.Save(Hash, result.Path);

				SetProgress(1f);
			} catch (Exception e) {
				Logger.LogError(e, tag: LogTag);
				SetProgress(0f);
			}

			_cache.Caching.Remove(this);
			_cts.Dispose();
			_cts = null;

			SendEvent();
		}

		/// <summary>
		/// Runs the download into a temporary file. The variants add their policy: only
		/// <see cref="CacheIdentifier"/> can follow a pending analysis.
		/// </summary>
		protected abstract UniTask<IDownloadResult> Download(CancellationToken token);

		/// <summary>Single download attempt of <see cref="Url"/>, verified against <see cref="Hash"/>.</summary>
		protected async UniTask<IDownloadResult> DownloadOnce(CancellationToken token)
			=> await Main.Instance.NetworkAPI.DownloadFile(Url, AssetDownloadOptions.For(Hash, SetProgress), token);
	}

	/// <summary>A raw URL and the hash its bytes must match.</summary>
	public sealed class CacheUrl : Cache {
		public CacheUrl(CacheManager cache, string url, string hash = null, CancellationToken token = default)
			: base(cache, token) {
			Url  = url;
			Hash = hash;
		}

		public override string Url { get; }

		public override string Hash { get; }

		protected override async UniTask<IDownloadResult> Download(CancellationToken token)
			=> await DownloadOnce(token);
	}

	/// <summary>
	/// The bundle of an asset when only its file is known. Downloaded as-is: without the asset
	/// identity a pending analysis cannot be followed, so a <c>409 ANALYSIS_PENDING</c> fails.
	/// </summary>
	public sealed class CacheAsset : Cache {
		public CacheAsset(CacheManager cache, IAssetFile file, CancellationToken token = default)
			: base(cache, token) {
			Url  = file?.Url;
			Hash = file?.CacheKey();
		}

		public override string Url { get; }

		public override string Hash { get; }

		protected override async UniTask<IDownloadResult> Download(CancellationToken token)
			=> await DownloadOnce(token);
	}

	/// <summary>
	/// The bundle of an asset resolved from its identifier. When the node answers
	/// <c>409 ANALYSIS_PENDING</c> the bundle status is refetched — the identifier is what makes that
	/// possible — until the analysis settles, then the download is retried.
	/// </summary>
	public sealed class CacheIdentifier : Cache {
		/// <summary>Downloads attempted while the file is analyzed before giving up.</summary>
		private const int MaxAnalysisRetries = 5;

		/// <summary>Upper bound of the wait for an analysis, so a stuck analysis cannot block a load.</summary>
		private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(10);

		private readonly Identifier _identifier;
		private readonly IAssetFile _bundle;

		public CacheIdentifier(CacheManager cache, Identifier identifier, IAssetFile bundle, CancellationToken token = default)
			: base(cache, token) {
			_identifier = identifier;
			_bundle     = bundle;
			Url         = bundle?.Url;
			Hash        = bundle?.CacheKey();
		}

		public override string Url { get; }

		public override string Hash { get; }

		protected override async UniTask<IDownloadResult> Download(CancellationToken token) {
			IDownloadResult result = null;

			for (var attempt = 1; attempt <= MaxAnalysisRetries; attempt++) {
				result = await DownloadOnce(token);

				if (result.Success
				    || !result.IsAnalysisPending
				    || _bundle == null
				    || !_identifier.IsValid()
				    || token.IsCancellationRequested)
					return result;

				Logger.LogDebug(
					$"The bundle at {Url} is still being analyzed"
					+ (string.IsNullOrEmpty(result.Error) ? string.Empty : $" ({result.Error})")
					+ $", refetching its status (attempt {attempt}/{MaxAnalysisRetries})…",
					tag: LogTag
				);

				if (!await WaitForAnalysis(token))
					return result;
			}

			return result;
		}

		/// <summary>Refetches the bundle status until the analysis settles.</summary>
		/// <returns><c>true</c> when the download can be retried, <c>false</c> when it cannot.</returns>
		private async UniTask<bool> WaitForAnalysis(CancellationToken token) {
			var deadline = DateTime.UtcNow + AnalysisTimeout;

			while (!token.IsCancellationRequested) {
				var snapshot = await Main.Instance.FetchStatus(_identifier, _bundle, token);
				var state    = snapshot?.Status?.Status ?? AssetState.Unknown;

				if (snapshot?.IsCompleted == true)
					return true;

				if (snapshot == null || state is AssetState.Awaiting or AssetState.Failed) {
					Logger.LogWarning($"Cannot wait for the analysis of {Url}: the file is '{state}'.", tag: LogTag);
					return false;
				}

				if (DateTime.UtcNow >= deadline) {
					Logger.LogWarning($"The analysis of {Url} did not complete within {AnalysisTimeout.TotalMinutes:0} minutes.", tag: LogTag);
					return false;
				}

				var delay = snapshot.RefetchAt == default
					? 2d
					: (snapshot.RefetchAt.ToUniversalTime() - DateTime.UtcNow).TotalSeconds;

				await UniTask.Delay(
					TimeSpan.FromSeconds(Math.Clamp(delay, 0.2d, 30d)),
					ignoreTimeScale: true,
					cancellationToken: token
				);
			}

			return false;
		}
	}
}
