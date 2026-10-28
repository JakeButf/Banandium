# Banandium
A rudamentary scene viewer for Donkey Kong Bananza

> [!IMPORTANT]
> Banandium does *not* provide any copyrighted assets. You must provide your own copy of the original game.

## Controls

- Hold right mouse button to pan the camera around
- WASD to move
- Space to move up, LShift to move down
- Scroll to change camera speed

## Building

`dotnet build Banandium.sln -c Release`

## Credits

Libraries:
- OpenTK
- Dear ImGui and ImGui.NET
- ImGui.NET_OpenTK_Sample
- BymlLibrary and SarcLibrary for BYML and SARC parsing
- ZstdSharp

References:
- [OdysseyDecomp](https://github.com/MonsterDruide1/OdysseyDecomp) for area behavior and geometry
- [OdysseyEditor](https://github.com/exelix11/OdysseyEditor) for scene editor reference
- [PhiveConverter](https://github.com/Nitr4m12/PhiveConverter) for PHIVE format reference
