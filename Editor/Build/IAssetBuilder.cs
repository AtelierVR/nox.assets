using System;
using Cysharp.Threading.Tasks;

namespace Nox.Assets.Editor {
	/// <summary>
	/// Builds the variants of one asset type. Implemented by the mod that owns the asset (avatars,
	/// worlds, mods…): the publisher only knows how to upload, so the build itself — and everything
	/// it needs — stays in the mod.
	/// </summary>
	public interface IAssetBuilder {
		/// <summary>
		/// Builds one file per platform of <see cref="AssetBuildContext.Platforms"/> into
		/// <see cref="AssetBuildContext.OutputPath"/> and returns them keyed by platform.
		/// </summary>
		UniTask<AssetBuildResult> Build(AssetBuildContext context);
	}

	/// <summary>
	/// Builds the variants through a delegate, so an asset type does not need a class of its own: the
	/// mod declares what to build where it declares the rest of its instructions (constraints, extra
	/// information, sections).
	/// </summary>
	public sealed class DelegateAssetBuilder : IAssetBuilder {
		private readonly Func<AssetBuildContext, UniTask<AssetBuildResult>> _build;

		/// <param name="build">Builds one file per platform of the context.</param>
		/// <param name="name">Name reported in the logs, for diagnostics.</param>
		public DelegateAssetBuilder(Func<AssetBuildContext, UniTask<AssetBuildResult>> build, string name = null) {
			_build = build ?? throw new ArgumentNullException(nameof(build));
			Name   = name ?? "builder";
		}

		public string Name { get; }

		public UniTask<AssetBuildResult> Build(AssetBuildContext context)
			=> _build(context);
	}
}
