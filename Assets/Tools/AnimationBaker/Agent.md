# Animation Baking Tool Guide

This document describes the Unity editor baking tool in this project:

`Tools/Animation/Animation To PNG Sequence Baker`

The tool bakes tweened `AnimationClip` motion into a Unity-ready sprite atlas. It is intended for converting layered 2D character animations, especially SPUM-style prefabs, into frame-based sprite animations.

## Human Operation Guide

### Basic Workflow

1. Open Unity.
2. Open the menu item `Tools/Animation/Animation To PNG Sequence Baker`.
3. In `Bake Sources`, select one or more character prefabs or prefab variants.
4. In `Animation Clips`, select one or more compatible animation clips. This list is empty until at least one bake source is selected.
5. Set `Output Frames` to the number of frames you want in the baked animation.
6. Set `Output Width` and `Output Height` for each frame cell in the atlas.
7. Choose an output folder with `Browse...`, or keep the default.
8. Click `Bake Selected`.
9. Use `Open Output Folder` or `Open Last Baked Folder` to inspect the generated atlas.

### Output

For each compatible `BakeSource + AnimationClip` pair, the tool writes:

```text
Assets/BakedAnimationFrames/<SourceName>/<ClipName>/<ClipName>_atlas.png
```

The generated atlas is imported as:

```text
Texture Type: Sprite
Sprite Mode: Multiple
Filter Mode: Point
Mip Map: Off
```

Each sliced child sprite is named:

```text
<ClipName>_0001
<ClipName>_0002
<ClipName>_0003
...
```

The numbering starts at `0001`, not `0000`.

### Fixed Naming Contract

These names are intentionally stable because future replacement automation depends on them:

```text
Atlas:          <OriginalClipName>_atlas.png
Child sprites:  <OriginalClipName>_0001, <OriginalClipName>_0002, ...
Future anim:    <OriginalClipName>_baked.anim
```

Do not casually rename these outputs unless you also update the replacement pipeline.

### Important Notes

- `Output Frames` is not FPS. It is the exact number of interpolated frames generated for each selected clip.
- If `Output Frames` is `12`, each clip produces 12 sliced sprites in the atlas.
- The tool samples evenly across the source clip duration.
- A bake source must include a prefab as the renderable carrier. A final game unit does not need to be a unique prefab if batch `sources` provide appearance configs.
- `Animation Clips` only shows clips compatible with at least one selected bake source.
- Multiple selected sources show the union of compatible clips, but bake execution only runs compatible source/clip pairs.
- The output folder must be inside `Assets`, because Unity needs to import the generated PNG as a sliced sprite asset.
- `Overwrite Existing` deletes the previous output folder for the current prefab/clip before writing the new atlas.

### Common Problems

If the bake is skipped, read the popup message and Console warning.

Common skip reasons:

```text
Clip binding paths do not exist on this prefab.
```

The selected clip probably does not match the selected prefab hierarchy.

```text
Rendered frames contained no visible pixels.
```

The prefab may require runtime initialization, the resolved sample root may be wrong, or the renderer/material chain is not visible to the bake camera.

```text
Atlas already exists and Overwrite Existing is disabled.
```

Enable `Overwrite Existing` or choose a new output folder.

## AI / Agent Instructions

### Current Tool File

The implementation lives here:

```text
Assets/Tools/AnimationBaker/Editor/AnimationToPngSequenceBakerWindow.cs
```

Treat this file as the source of truth for the baking workflow.

### Core Behavior

The tool should:

- Discover usable prefab/prefab variant bake sources under `Assets`.
- Discover `AnimationClip` assets under `Assets`.
- Let the user select bake sources and compatible clips from filtered tree lists.
- Keep the clip list empty until at least one bake source is selected.
- Show the union of clips compatible with selected sources.
- Bake only compatible source/clip pairs; do not use a raw cartesian product.
- Instantiate selected source prefabs temporarily for baking.
- Apply appearance config through `IAnimationBakeAppearanceApplier` when a batch source provides one.
- Resolve the best internal sample root for each clip instead of assuming the prefab root is the animation root.
- Sample each selected clip into exactly `Output Frames` frames.
- Render each sampled frame to an offscreen `RenderTexture`.
- Combine the rendered frames into one atlas PNG.
- Import the PNG as `SpriteImportMode.Multiple`.
- Slice the atlas into child sprites named from `0001`.
- Generate `<OriginalClipName>_baked.anim` from the sliced atlas sprites.

### Automation Entry Point

Agents can invoke the tool from Unity batchmode:

```text
Unity.exe -batchmode -quit -projectPath <ProjectPath> -executeMethod AnimationToPngSequenceBakerWindow.BakeFromConfig -bakerConfig <ConfigJsonPath>
```

The config format is demonstrated here:

```text
Assets/Tools/AnimationBaker/AnimationBakerConfig.example.json
```

If `autoMatchClips` is `true`, provide one or more `sources` and leave `clipPaths` empty. The tool will locally select compatible clips by comparing clip binding paths against prefab transform paths.

If exact control is needed, provide both `sources` and `clipPaths`.

Preferred source config:

```json
{
  "name": "Knight_01",
  "outputName": "Knight_01",
  "prefabPath": "Assets/Characters/BaseHuman.prefab",
  "appearanceConfigPath": "Assets/Characters/Configs/Knight_01.asset"
}
```

Legacy `prefabPaths` and `prefabGuids` are still supported and are converted into plain bake sources internally.

If `appearanceConfigPath` is present, another editor script must implement:

```csharp
public interface IAnimationBakeAppearanceApplier
{
    bool CanApply(UnityEngine.Object config);
    void Apply(GameObject instance, UnityEngine.Object config);
}
```

The baker intentionally fails that source if no applier can handle the config, because silently baking the base prefab would produce incorrectly named output.

### Do Not Regress These Fixes

These issues were already found and fixed. Do not undo them.

- Do not calculate output frame count as `clip.length * fps`; short clips such as SPUM `0_move` would produce only 4 frames.
- Do not number frames from `0000`; output child sprites must start at `0001`.
- Do not use `Camera.cullingMask = ~0`; that can capture objects from the open scene.
- Do not rely on `PreviewScene` rendering for this workflow unless you re-verify visibility. The current approach uses a hidden temporary object in the active editor scene plus an isolated layer.
- Do not silently accept pure-color or empty frames as success.
- Do not output only loose PNG frames when the user expects Unity-ready atlases.
- Do not change the naming contract without updating future replacement tooling.

### Layer Isolation

The tool finds an unused layer among currently loaded scenes and moves the temporary prefab instance to that layer. The bake camera only renders that layer.

This is necessary because a camera can otherwise capture content from the open editor scene.

### Sample Root Resolution

SPUM prefabs may contain multiple internal roots and multiple animators. Source clips often bind paths such as:

```text
Root
Root/BodySet/P_Body
Root/BodySet/P_Body/HeadSet/P_Head
```

Those paths may be relative to an internal child object, not the outer prefab root.

The tool must resolve the best matching internal transform root before calling:

```csharp
AnimationMode.SampleAnimationClip(sampleRoot.gameObject, clip, time);
```

### Sprite Atlas Import

Atlas import must remain compatible with Unity sprite workflows:

```csharp
importer.textureType = TextureImporterType.Sprite;
importer.spriteImportMode = SpriteImportMode.Multiple;
importer.alphaIsTransparency = true;
importer.mipmapEnabled = false;
importer.filterMode = FilterMode.Point;
importer.wrapMode = TextureWrapMode.Clamp;
```

Each `SpriteMetaData.rect` maps one frame cell in the atlas.

### Future Replacement Pipeline

Future automation will likely:

1. Read `<OriginalClipName>_atlas.png`.
2. Use child sprites named `<OriginalClipName>_0001...`.
3. Use the generated `<OriginalClipName>_baked.anim`.
4. Replace original tween clips in controllers or override controllers with baked sprite clips.

The naming contract is therefore part of the architecture, not a cosmetic detail.

### Generated Baked Animation Clips

The tool generates an `.anim` clip from the sliced atlas:

```text
<OriginalClipName>_baked.anim
```

That generated clip animates a single `SpriteRenderer.sprite` property using the atlas child sprites in order.

After that, a separate replacement tool can map:

```text
<OriginalClipName>.anim -> <OriginalClipName>_baked.anim
```

and batch update Animator Controllers or Animator Override Controllers.
