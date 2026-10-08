# Third-party assets

## Scan textures (CC0)

Downloaded from [Poly Haven](https://polyhaven.com), stored under
`Assets/Resources/Artwork/`. All are CC0 1.0 Universal — public domain, no
attribution required, no restriction on commercial use or redistribution.

Poly Haven's licence, verbatim:

> "Our assets are all licensed as [CC0](https://creativecommons.org/publicdomain/zero/1.0/),
> which is effectively Public Domain even in jurisdictions that do not support the
> Public Domain. You can use our assets for any purpose, including commercial work.
> You do not need to give credit or attribution."

| Folder | Poly Haven asset | Maps | Used for |
| --- | --- | --- | --- |
| `Ground/` | `brown_mud_02` | diffuse, normal | Battlefield ground |
| `Road/` | `gravel_road` | diffuse, normal | Road surface |
| `Rubble/` | `rubble` | diffuse, normal | Ruined walls |

All at 2K. Normal maps are OpenGL-style (+Y up), which is what Unity's Standard
shader expects natively — no channel flip is applied at import.

Roughness and AO maps were deliberately **not** shipped. Mud, gravel and rubble are
dielectrics, so metallic is zero and gloss is a constant per material. That is
physically correct and saved about 8 MB.

If these files are ever removed, the scene still builds: `VisualMaterials.Textured`
falls back to a generated seamless noise texture, and
`VisualMaterials.ArtworkPresent` reports whether the real art loaded.

---

## Vehicle models: none shipped yet

No tank or aircraft models are included. Every vehicle in the game is still built
from Unity primitives in `TankView` and `AircraftView`.

A search for free WW2 vehicle models turned up **no CC0 option at all** for either
tanks or aircraft. The realistic options are:

| Source | Licence | Notes |
| --- | --- | --- |
| [Newc42 low-poly German tanks](https://newc-42.itch.io/german-low-poly-wwii-tanks) | **CC0**, verified on the itch.io licence field | Tiger and Panzer III only. No T-34, no Panzer IV. Stylised, not realistic. |
| [Newc42 nature pack](https://newc-42.itch.io/low-poly-nature-assets) | **CC0**, verified | Trees, rocks, clouds. Good tree-line candidate. |
| Sketchfab (various authors) | **CC-BY**, attribution required | Best per-aircraft poly counts found: Spitfire 3.4k, Me 262 2.3k, P-51 5.3k, Meteor 12.2k, Bf 109 7.9k tris. |
| ambientCG | **CC0**, verified | Good ground/road/rubble alternatives and tank-track marks. |
| Kenney Nature Kit | **CC0**, verified | Trees and rocks. No military vehicles. |

### Licences to avoid

- **Free3D "free" models** are almost all *Download Personal Use License* only.
- **TurboSquid** free models are frequently labelled *Editorial Use*, which forbids
  commercial use.
- **CC-BY-SA** (share-alike) is viral on derivatives — one A6M5 Zero is CC-BY-SA and
  should not be used.
- **CC-BY-NC** forbids commercial use entirely.
- **Quaternius** is no longer CC0. As of 28 August 2026 it uses the Quaternius Asset
  License, which permits commercial games but forbids redistributing the assets.
  Older packs still carry CC0 on their individual pages.
- **Ripped game assets** are prevalent on Sketchfab. Anything sourced from War
  Thunder, World of Tanks or World of Warplanes is likely rights-infringing whatever
  licence badge the uploader selected.

Sketchfab's licence filter in their data API is unreliable — filtering by `cc0`
returns nothing and `cc-by` errors. Individual model licence must be checked per
model.

### Also worth knowing

Sketchfab's download API serves **glTF only**, explicitly not FBX or OBJ. To get
FBX you must download through the web interface, and even then you get whatever the
uploader provided.

Prefer FBX over `.unitypackage` for third-party packs: a package built by someone
using URP or HDRP will drag shader graphs into this built-in-pipeline project.

---

## Procedural assets

Everything not listed above is generated in code:

- Ground noise texture — seamless value-noise fBm, `VisualMaterials`
- Sky — Unity's built-in `Skybox/Procedural` shader
- All vehicle and scenery geometry — `GameObject.CreatePrimitive`
