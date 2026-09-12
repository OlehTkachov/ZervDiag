using System.Runtime.CompilerServices;
using CraneCAN.Core.Storage;

internal static class NetworkProfileCompatibilitySmokeTests
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var path = Path.Combine(Path.GetTempPath(), "cranecan-old-profile-" + Guid.NewGuid().ToString("N") + ".craneprofile");
        try
        {
            File.WriteAllText(path,
                "{\"profileId\":\"49000000-0000-0000-0000-000000000001\",\"profileSchemaVersion\":1," +
                "\"manufacturer\":\"Legacy\",\"model\":\"Machine\",\"machineName\":\"Old profile\"," +
                "\"canBusName\":\"CAN1\",\"canType\":\"Classical CAN\",\"knownSignals\":[]," +
                "\"experimentalSignals\":[],\"rejectedCandidates\":[],\"notes\":\"\",\"programVersion\":\"0.7.0\"}");

            var profile = GuidedJsonCodec.LoadProfileAsync(path).GetAwaiter().GetResult();
            if (profile.Network is not null)
                throw new InvalidOperationException("Legacy Machine Profile without network section must load with Network=null.");
            if (profile.MachineName != "Old profile")
                throw new InvalidOperationException("Legacy Machine Profile fields were not preserved.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
