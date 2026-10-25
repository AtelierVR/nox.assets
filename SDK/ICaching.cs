using Cysharp.Threading.Tasks;
using UnityEngine.Events;

namespace Nox.Assets {
	/// <summary>
	/// A cache-backed download: progress, cancellation and de-duplication of concurrent requests
	/// targeting the same file.
	/// </summary>
	public interface ICaching {
		/// <summary>Cancels the download.</summary>
		void Cancel();

		/// <summary>Runs the download and stores the file in the asset cache.</summary>
		UniTask Start();

		/// <summary>Waits for the download started by another caller to settle.</summary>
		UniTask Wait();

		/// <summary><c>true</c> while the transfer (or the analysis wait) is in flight.</summary>
		bool IsRunning { get; }

		/// <summary>Progress ratio between <c>0</c> and <c>1</c>.</summary>
		float Progress { get; }

		/// <summary>Raised with the progress ratio on every update.</summary>
		UnityEvent<float> OnProgress { get; }
	}
}
