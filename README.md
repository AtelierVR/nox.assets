# Nox — Assets

Shared asset pipeline for [Nox](https://nox.hactazia.fr): one cache, one downloader and one editor
publisher for every asset type (avatars, worlds, mods…).

## What it does

- **Runtime cache** — resolves the endpoint of an asset from the node (`/api/configs`), downloads it
  once into a local cache and rebuilds the cache when the server invalidates the entry.
- **Asset API client** — implements `IAssetsAPI` on top of `nox.network`: fetch, search, release
  files, upload/download, images. It keeps the `409 ANALYSIS_PENDING` retry loop.
- **Editor publisher** — a generic panel (`AssetPublisherPanel`) driven by an `AssetPublisherSource`:
  attach an asset, edit its information, manage its images, pick a version, publish the release and
  upload one bundle per platform.
- **Editor builder** — a generic panel (`AssetBuilderPanel`) driven by an `AssetBuilderSource`: pick
  the descriptor, choose the platforms and the output folder, build.
- **Shared helpers** — notifications, platform picker, image grid, publish/build contexts, upload
  rules, release notes.

A mod therefore only declares *instructions* (its `*PublisherSource` / `*BuilderSource` plus the
build call): the whole UI and the pipeline stay here.

## Layout

```
SDK/                 interfaces (ICaching, ICacheEntry, IAssetCacheAPI)
CCK/                 shared DTOs (IdentifierCache, UrlCache)
Runtime/             Main, Assets/AssetsClient, Assets/AssetDownload, Caching/Cache(+Manager)
Editor/              Editor entry point
Editor/Build/        AssetBuildContext, AssetBuildResult, IAssetBuilder
Editor/Publish/      AssetPublisher, AssetUploader, AssetRules, AssetPublish*
Editor/Notifications/ AssetNotification, AssetPublisherNotifications
Editor/UI/Common/    settings, info rows, selection field, platform picker, image grid
Editor/UI/Publisher/ generic publisher panel (+ source/section/foldout)
Editor/UI/Builder/   generic builder panel (+ source)
```

## Requirements

- Unity 6000.x
- [`nox.cck`](https://github.com/AtelierVR/nox.cck), [`nox.network`](https://github.com/AtelierVR/nox.network),
  [`nox.users`](https://github.com/AtelierVR/nox.users), [`nox.editor.panel`](https://github.com/AtelierVR/nox.editor.panel),
  and [UniTask](https://github.com/Cysharp/UniTask)

## Install

```json
"nox.assets": "https://github.com/AtelierVR/nox.assets.git"
```

Mods reference it through their `nox.mod.json`:

```json
{ "id": "assets", "type": "depends", "version": ">=1.0.0", "register": "git+https://github.com/AtelierVR/nox.assets.git" }
```

## License

GPL-3.0 — see [LICENSE](LICENSE).
