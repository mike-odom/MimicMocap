# Test media and optional assets

Some things the tests and the character menu use are not in the repository because they cannot be
redistributed. Everything degrades gracefully without them: tests that need a missing file report
`Ignored` with the file name, and the character menu simply shows fewer portals.

## Test videos (optional)

Put any `.mp4`/`.mov`/`.webm` clips of a single person (or two) in a folder named `TestVideos` next to
the project folder (`..\TestVideos`) or next to the built player. `VideoPipelineTests` and
`StabilityTests` pick them up, as do the `P`/`N`/`B` hotkeys and `-video <file>`. `StabilityTests`
prefers a clip whose name contains `Kung`; otherwise it takes the first one.

## Test images (optional)

`Assets/MimicMocap/Tests/Resources/TestImages/` is git-ignored. The smoke tests look for these
names (PNG or JPG, any size):

| File | Used for |
|---|---|
| `Kung_Fu_Girl_1` | BlazePose/Maxine single frame, scene render, two-person composite (left) |
| `Kung_Fu_Girl_2_1` | second frame |
| `bye_bye_bye_-_Trainer_1` | scene render, two-person composite (right) |
| `hand_reference` | a clear photo of one hand for the finger tests (the tests place the wrist/knuckle joints by hand, see `HandTrackingSmokeTests`) |

Any full-body frame of your own works for the first three; the hand test expects the hand to fill a good
part of the frame. After adding files, set their texture import to Read/Write enabled is not required
(the tests copy them through a RenderTexture).

## Models

None of the ONNX models are in the repository. `tools/get_models.ps1` downloads them (BlazePose and the MediaPipe hand
landmarker from Unity's Hugging Face repositories, RTMPose-m hand5 from OpenMMLab's ONNX SDK package) into
`Assets/MimicMocap/Models` and checks each against the SHA-256 of the copy the project was tested with. Run it before
opening the project; if Unity was opened first and dropped the models' `.meta` files, run it and then **MimicMocap > Create
Scene**. Without the RTMPose hand model the app runs MediaPipe alone and `HandFallbackTests` reports Ignored.

## Character rigs (optional)

`MimicMocap > Setup Characters` places every Humanoid FBX found under `Assets/MimicMocap/Characters/`.
The CC0 KayKit skeletons are included. Two rigs used during development are not:

- Adobe Mixamo "Skeletonzombie_T_Avelange" (download from mixamo.com, drop the FBX with embedded
  textures into `Characters/Mixamo/`).
- The "Skeleton animated" Biped rig (`Characters/SkeletonAnimated/Skeleton_Animated.fbx` plus its
  `Textures/Skeleton_Body_BaseColor.png`).

Run Setup Characters again after adding either; the menu order, labels and head scales for these two are
already in `MimicCharacterTools.k_KnownCharacters`.

## MMD models (PMX)

MMD models work too, converted to FBX with Blender and the MMD Tools add-on, then mapped by their Japanese
bone names on import (`MimicCharacterTools.FixMmdMapping`: deform `D` bones for the legs, full-width
digit finger names, Blender's `.L`/`.R` suffixes). Blender 4.2 or newer plus MMD Tools v4.5+ is needed; a
portable Blender zip works without admin rights (the extension also needs `pip install opencc-python-reimplemented`
into Blender's own Python).

```
blender -b --python tools/pmx_to_fbx.py -- "<model>.pmx" "Assets/MimicMocap/Characters/<Name>/<Name>.fbx" 0.08
```

The script prints the bone tree and materials, exports mesh and armature only (no morphs, no physics) and
copies the base textures into a `Textures` folder beside the FBX. Setup Characters then copies the materials out of
the FBX, makes them two-sided and matte with no specular, lets the base map glow a little (MMD colours are flat
toon colours that otherwise read as untextured grey under the stage lights), hides parts that ship with alpha 0
(blush, tears, glasses that a material morph would normally show) and alpha-clips hair. MMD rigs also receive no
shadows: alpha-clipped hair threw hard, flickering shadows across the face as the model moved (they still cast onto
the floor).

Some models skin their mesh to bones that MMD only moves through IK: Sour's Miku has 腕W, ひじW and 首W, which
point from the shoulder at the elbow, from the elbow at the wrist and from the neck at the head, and the real 腕/ひじ
bones carry no weights at all. An FBX has no IK, so in Unity those bones never moved and the arm mesh stayed in
the A-pose while the hands went off on their own. `pmx_to_fbx.py` finds every weighted bone with an IK constraint,
moves its weights (and its child bones, such as the wrist cube hanging off ひじW) to the plain bone that starts at the
same joint and leads to the IK target, and deletes it; the export log lists each hand-over. Two helper scripts show
what a model is made of before converting it:

```
blender -b --python tools/pmx_weights.py -- "<model>.pmx"             # mesh weight per bone
blender -b --python tools/pmx_bones.py -- "<model>.pmx" 腕 ひじ 手 首  # bone tree with MMD flags, filtered by name
```

Sour's Miku (`Characters/SourMiku`, "Sour式初音ミク Ver.1.02") was set up this way during development; its readme
forbids commercial use, so it is git-ignored like the other downloaded rigs. `CharacterLookTests` renders every
rig standing and in a gesture (one arm up, one elbow bent) to `Logs/PoseCheck/character_<name>[_gesture].png`,
which is how a rig whose mesh does not follow its bones shows up.

## After cloning

The committed scene references every rig that was placed when it was last saved, including the ones not in
the repository, so it shows those slots as missing prefabs until you run **MimicMocap > Setup Characters**
(or `tools/run_pipeline.ps1`, which regenerates the scene and places whatever rigs are present).
