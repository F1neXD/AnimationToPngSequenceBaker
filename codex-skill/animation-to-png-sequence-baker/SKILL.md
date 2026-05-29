---
name: animation-to-png-sequence-baker
description: Use this when the user wants to run or automate the Unity AnimationToPngSequenceBaker tool, bake tweened Unity AnimationClip motion into sprite sheet atlases, auto-match prefabs to compatible clips, or generate baked sprite animation clips from Unity 2D prefab animations.
---

# AnimationToPngSequenceBaker Skill

Use the Unity editor tool installed at:

```text
Assets/Tools/AnimationBaker/Editor/AnimationToPngSequenceBakerWindow.cs
```

The Unity menu path is:

```text
Tools/Animation/Animation To PNG Sequence Baker
```

## Preferred Automation Flow

When the user asks to use this tool programmatically:

1. Locate the Unity project root.
2. Confirm the tool exists under `Assets/Tools/AnimationBaker`.
3. Create a JSON config file based on the user's requested prefab(s), clip(s), output path, frame count, and size.
4. Prefer local matching by setting `autoMatchClips` to `true` when the user provides prefabs but not explicit clips.
5. Invoke Unity batchmode:

```text
Unity.exe -batchmode -quit -projectPath <ProjectPath> -executeMethod AnimationToPngSequenceBakerWindow.BakeFromConfig -bakerConfig <ConfigJsonPath>
```

If Unity cannot be found on PATH, look for an installed Unity editor under common Unity Hub locations or ask the user for the Unity executable path.

## Config Format

```json
{
  "outputFolder": "Assets/BakedAnimationFrames",
  "outputFrames": 12,
  "outputWidth": 256,
  "outputHeight": 256,
  "boundsPaddingPercent": 0.08,
  "includeInactiveChildren": true,
  "overwriteExistingFiles": true,
  "autoMatchClips": true,
  "prefabPaths": [
    "Assets/Path/To/Character.prefab"
  ],
  "clipPaths": []
}
```

Use `prefabGuids` and `clipGuids` only when GUIDs are already known. Asset paths are easier to inspect and debug.

## Matching Rules

The tool performs deterministic local matching. It does not call an external model API.

Matching compares source clip binding paths against transform paths under possible prefab roots. This is especially important for SPUM-style prefabs, where clips may be relative to an internal `Root` object rather than the outer prefab root.

Use exact `clipPaths` when the user explicitly chooses clips. Use `autoMatchClips` when the user wants the tool to infer the prefab's compatible animations.

## Outputs

For each successful prefab and clip pair:

```text
Assets/BakedAnimationFrames/<PrefabName>/<ClipName>/<ClipName>_atlas.png
Assets/BakedAnimationFrames/<PrefabName>/<ClipName>/<ClipName>_baked.anim
```

Atlas child sprites are named:

```text
<ClipName>_0001
<ClipName>_0002
...
```

The numbering starts at `0001`.

## Important Constraints

- `outputFrames` is the exact number of baked frames per clip, not FPS.
- Output must stay inside `Assets` so Unity can import generated atlases and `.anim` clips.
- Do not rename `_atlas`, `_baked`, or numbered child sprite outputs; replacement automation depends on this contract.
- If the bake reports no visible pixels, do not treat it as success. Inspect prefab initialization, renderer visibility, and clip compatibility.

