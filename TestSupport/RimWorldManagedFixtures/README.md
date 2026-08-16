# RimWorld managed assembly fixtures

These compile-only stubs provide the narrow `Assembly-CSharp.dll` and
`UnityEngine.CoreModule.dll` type surface referenced by `Source/Mod`.
They let the offline development-publish matrix build and deploy the real
mod assembly without requiring proprietary RimWorld files or a checkout
nested beside a game installation. They are never copied into a release or
loaded by FakeRimWorld.
