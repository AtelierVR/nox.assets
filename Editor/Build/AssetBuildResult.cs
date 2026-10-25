using System;
using System.Collections.Generic;
using Nox.CCK.Utils;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Outcome of a build: the variants it produced, keyed by platform. A successful result must
	/// declare one existing file per requested platform; the publisher checks it before reserving
	/// any slot.
	/// </summary>
	public sealed class AssetBuildResult {
		private AssetBuildResult(bool success, string message, Dictionary<Platform, string> outputs) {
			Success = success;
			Message = message;
			Outputs = outputs ?? new Dictionary<Platform, string>();
		}

		public bool Success { get; }

		/// <summary>Failure reason, or a summary of the build when it succeeded.</summary>
		public string Message { get; }

		/// <summary>Built file per platform.</summary>
		public IReadOnlyDictionary<Platform, string> Outputs { get; }

		/// <summary>Successful build carrying one file per platform.</summary>
		public static AssetBuildResult Ok(Dictionary<Platform, string> outputs, string message = null)
			=> new(true, message, outputs);

		public static AssetBuildResult Failed(string message)
			=> new(false, message, null);
	}
}
