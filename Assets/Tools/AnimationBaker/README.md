# Animation Baker

Unity editor tool for baking tweened 2D `AnimationClip` motion into sliced sprite sheet atlases.

Open it from:

```text
Tools/Animation/Animation To PNG Sequence Baker
```

## What It Exports

For each selected prefab and animation clip pair, the tool creates:

```text
<ClipName>_atlas.png
```

The atlas is imported as a Unity `Sprite Mode: Multiple` texture and sliced into child sprites named:

```text
<ClipName>_0001
<ClipName>_0002
<ClipName>_0003
...
```

The number of child sprites is controlled by `Output Frames`.

The tool also generates:

```text
<ClipName>_baked.anim
```

That animation clip plays the sliced child sprites on a single `SpriteRenderer`.

## Default Output

```text
Assets/BakedAnimationFrames/<PrefabName>/<ClipName>/<ClipName>_atlas.png
```

The output folder can be changed from the tool window, but it must stay inside the project's `Assets` folder so Unity can import the generated atlas.

## Notes

- This tool is designed for 2D character prefabs with `SpriteRenderer` based visuals.
- It resolves the best internal animation root automatically for nested prefabs such as SPUM characters.
- It isolates the bake camera on a temporary layer so it does not capture the currently open scene.
- Naming is intentionally stable for future automated replacement workflows.
