using UnrealBuildTool;

public class VHReceiver : ModuleRules
{
    public VHReceiver(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine",
            "Sockets",
            "Networking",
            "Json",
            "JsonUtilities",
        });
    }
}
