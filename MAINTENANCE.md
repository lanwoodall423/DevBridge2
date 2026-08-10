# DevBridge2 maintenance

The coordinator is a small named-pipe server/client in `Source/Coordinator/Program.cs`. One server owns `Runtime/state.json`, active leases, and the RimWorld process. CLI invocations connect to that server, so a caller timing out does not cancel a pending restart.

The RimWorld assembly is `Source/Mod/DevBridge2Mod.cs`. It reads `DEVBRIDGE_ROOT`, `DEVBRIDGE_LAUNCH_ID`, and `DEVBRIDGE_GENERATION`, then atomically writes `Runtime/readiness.json` once `GenScene.InPlayScene`, `Current.Game`, and `Find.CurrentMap` are all available.

Build from the mod root:

```text
dotnet publish Source\Coordinator\DevBridge.Coordinator.csproj -c Release -r win-x64 --self-contained false -o Coordinator
dotnet build Source\Mod\DevBridge2.csproj -c Release -p:RimWorldManagedDir="C:\Games\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed"
```

The mod build output is `1.6\Assemblies\DevBridge2.dll`.
