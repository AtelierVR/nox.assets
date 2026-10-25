using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Network;
using Nox.CCK.Network.Assets;
using Nox.CCK.Utils;
using Nox.Network.Assets;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Assets.Runtime.Assets {
	/// <summary>
	/// Cache-backed download of a single URL: streams to a <c>.part</c> file next to the cache
	/// entry, verifies the hash when one is known, then moves the file under its digest.
	/// Instances are de-duplicated by the owning <see cref="AssetCache"/>.
	/// </summary>
	internal class AssetDownload : IAssetDownload {
		private readonly AssetCache                          _cache;
		private readonly IReadOnlyDictionary<string, string> _headers;
		private readonly CancellationToken                   _externalToken;
		private readonly List<Action<float, ulong>>          _callbacks = new();
		private readonly UnityEvent<float>                   _onProgress = new();

		private CancellationTokenSource _cts;
		private float                   _progress;
		private string                  _stored;
		private string                  _error;
		private string                  _errorCode;
		private long                    _statusCode;
		private bool                    _completed;

		public AssetDownload(
			AssetCache cache,
			string url,
			IAssetHash hash,
			IReadOnlyDictionary<string, string> headers,
			Action<float, ulong> progress,
			CancellationToken token
		) {
			_cache         = cache;
			Url            = url;
			Hash           = hash;
			_headers       = headers;
			_externalToken = token;

			if (progress != null)
				_callbacks.Add(progress);
		}

		public string Url { get; }

		public IAssetHash Hash { get; }

		public float Progress
			=> _progress;

		public long StatusCode
			=> _statusCode;

		public string ErrorCode
			=> _errorCode;

		public string Error
			=> _error;

		public bool IsRunning
			=> _cts is { IsCancellationRequested: false };

		public bool IsCompleted
			=> _completed;

		public UnityEvent<float> ProgressEvent
			=> _onProgress;

		/// <summary>Attaches another progress listener to the shared download.</summary>
		internal void Subscribe(Action<float, ulong> progress) {
			if (progress != null)
				_callbacks.Add(progress);
		}

		public void Cancel() {
			if (IsRunning)
				_cts.Cancel();
		}

		public async UniTask<string> Start() {
			if (_completed)
				return _stored ?? _cache.PathFor(Hash);

			if (IsRunning)
				return await Wait();

			if (AssetHash.Has(Hash) && _cache.Has(Hash)) {
				_stored    = _cache.PathFor(Hash);
				_completed = true;
				SetProgress(1f);
				return _stored;
			}

			_cts = new CancellationTokenSource();
			var token = _externalToken.CanBeCanceled
				? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _externalToken).Token
				: _cts.Token;

			_cache.Track(this);

			var name    = AssetHash.ToFileName(Hash) ?? Guid.NewGuid().ToString("N");
			var partial = Path.Combine(_cache.Root, name + ".part");

			try {
				SetProgress(0f);

				var request = RequestExtension.To(Url);
				request.downloadHandler = new DownloadHandlerFile(partial) { removeFileOnAbort = true };

				if (_headers != null)
					foreach (var header in _headers)
						if (!string.IsNullOrEmpty(header.Value))
							request.SetRequestHeader(header.Key, header.Value);

				request.HandleDownloadProgress((value, size) => SetProgress(value, size), token);

				await request.Send(token);

				_statusCode = request.responseCode;

				if (!request.Ok()) {
					// The answer carries the reason (e.g. ANALYSIS_PENDING while the file is
					// analyzed): keep it so the caller can decide to refetch and retry.
					_errorCode = DownloadEnvelope.ReadCode(request, partial, out var message);

					TryDelete(partial);

					throw new Exception(
						message
						?? $"Download failed with status {request.responseCode}"
						+ (string.IsNullOrEmpty(request.error) ? string.Empty : $" ({request.error})")
					);
				}

				var computed = await UniTask.RunOnThreadPool(
					() => AssetHash.Parse(Hashing.HashFile(AssetHash.Sha256, partial)),
					cancellationToken: token
				);

				if (AssetHash.Has(Hash) && !AssetHash.Matches(Hash, computed))
					throw new Exception($"Hash mismatch (expected {Hash.Value}, got {computed.Value})");

				var final = _cache.PathFor(AssetHash.Has(Hash) ? Hash : computed);

				if (string.IsNullOrEmpty(final))
					throw new Exception("Cannot resolve a cache path for the downloaded file");

				if (File.Exists(final))
					File.Delete(final);

				File.Move(partial, final);

				_stored    = final;
				_completed = true;
				SetProgress(1f);
				return final;
			} catch (OperationCanceledException) {
				_error = "Canceled";
				TryDelete(partial);
				return null;
			} catch (Exception ex) {
				_error = ex.Message;
				Logger.LogError(ex, tag: AssetCache.LogTag);
				TryDelete(partial);
				return null;
			} finally {
				_cache.Untrack(this);
				_cts?.Dispose();
				_cts = null;
			}
		}

		public async UniTask<string> Wait() {
			if (IsRunning)
				await UniTask.WaitUntil(() => !IsRunning, cancellationToken: _externalToken);

			return _completed ? _stored : null;
		}

		private void SetProgress(float value, ulong size = 0) {
			_progress = Mathf.Clamp01(value);
			_onProgress.Invoke(_progress);

			foreach (var callback in _callbacks) {
				try {
					callback.Invoke(_progress, size);
				} catch (Exception ex) {
					Logger.LogError(ex, tag: AssetCache.LogTag);
				}
			}
		}

		private static void TryDelete(string path) {
			try {
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
					File.Delete(path);
			} catch (Exception ex) {
				Logger.LogWarning($"Could not clean '{path}': {ex.Message}", tag: AssetCache.LogTag);
			}
		}
	}
}
