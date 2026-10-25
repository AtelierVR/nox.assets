using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;

namespace Nox.Assets.Editor {
	/// <summary>
	/// What a publisher hands to the builder of an asset type: where to write the variants, which
	/// platforms to produce, and how to report the progress. The mod keeps its own state (descriptor,
	/// scene, target list) in <see cref="State"/> and only has to return the produced files.
	/// </summary>
	public sealed class AssetBuildContext {
		public AssetBuildContext(
			string outputPath,
			Platform[] platforms,
			Engine engine,
			object state,
			Action<float, string> progress = null,
			CancellationToken token = default
		) {
			OutputPath = outputPath;
			Platforms  = platforms ?? Array.Empty<Platform>();
			Engine     = engine;
			State      = state;
			Progress   = progress;
			Token      = token;
		}

		/// <summary>Directory the variants must be written to (always ends with a separator).</summary>
		public string OutputPath { get; }

		/// <summary>Platforms the release must contain; the builder reports one file per entry.</summary>
		public Platform[] Platforms { get; }

		/// <summary>Engine the bundles are built for.</summary>
		public Engine Engine { get; }

		/// <summary>Mod-specific state (descriptor, scene, targets…), opaque to the publisher.</summary>
		public object State { get; }

		/// <summary>Cancellation of the whole publish operation.</summary>
		public CancellationToken Token { get; }

		private Action<float, string> Progress { get; }

		/// <summary>Reports the build progress (<c>0..1</c>) with a message.</summary>
		public void Report(float progress, string message)
			=> Progress?.Invoke(progress, message);
	}
}
