# AnimationToPngSequenceBaker

A Unity editor tool for baking tweened 2D `AnimationClip` animations into Unity-ready sliced sprite sheet atlases.

The tool was built for workflows where layered 2D character animations, such as SPUM-style prefab rigs, need to be converted into frame-based sprite animation assets.

## Features

- Batch select prefabs and animation clips from filtered editor lists.
- Bake tweened animation into a fixed number of interpolated frames.
- Output one sprite sheet atlas per `Prefab + AnimationClip` pair.
- Import generated atlases as `Sprite Mode: Multiple`.
- Automatically slice child sprites for Unity animation workflows.
- Automatically generate `<ClipName>_baked.anim` from the sliced atlas sprites.
- Show the last bake result list with reveal/select buttons.
- Persist common settings between editor sessions.
- Isolate the bake camera so it does not capture the currently open scene.
- Resolve nested animation roots for prefabs whose clips are not bound to the outer prefab root.

## Installation

### Option 1: Import UnityPackage

Download or copy:

```text
AnimationBaker.unitypackage
```

Then import it into your Unity project:

```text
Assets > Import Package > Custom Package...
```

### Option 2: Copy Source Folder

Copy this folder into your Unity project:

```text
Assets/Tools/AnimationBaker
```

## Usage

Open the tool from Unity:

```text
Tools/Animation/Animation To PNG Sequence Baker
```

Basic workflow:

1. Select one or more prefabs in the `Prefabs` list.
2. Select one or more clips in the `Animation Clips` list.
3. Set `Output Frames`.
4. Set the per-frame output size.
5. Choose an output folder inside `Assets`, or use the default.
6. Click `Bake Selected`.

## Output

Default output path:

```text
Assets/BakedAnimationFrames/<PrefabName>/<ClipName>/<ClipName>_atlas.png
```

The generated atlas is imported as:

```text
Texture Type: Sprite
Sprite Mode: Multiple
Filter Mode: Point
Mip Map: Off
```

Child sprite naming:

```text
<ClipName>_0001
<ClipName>_0002
<ClipName>_0003
...
```

Frame numbering starts at `0001`.

The tool also generates:

```text
<ClipName>_baked.anim
```

The generated clip animates a single `SpriteRenderer.sprite` property using the sliced atlas sprites in order.

## Naming Contract

The naming rules are intentionally stable for future automated replacement workflows:

```text
Atlas:          <OriginalClipName>_atlas.png
Child sprites:  <OriginalClipName>_0001, <OriginalClipName>_0002, ...
Future anim:    <OriginalClipName>_baked.anim
```

Do not change these names casually if you plan to build automated replacement tooling later.

## Notes For AI Agents

This repository includes:

```text
Assets/Tools/AnimationBaker/Agent.md
```

That file contains maintenance notes for future AI-assisted development, including known pitfalls, architectural constraints, and replacement-pipeline assumptions.

## Unity Version

Developed in Unity `2022.3.62f3c1`.

The tool is an editor script and should be usable in nearby Unity 2022 LTS versions, but other versions have not been verified.
