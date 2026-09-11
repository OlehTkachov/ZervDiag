using System.Runtime.CompilerServices;
using CraneCAN.Core.Profiles;

internal static class Hc4900ProfileSmokeTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        var system = Hc4900Profile.SystemProfile;
        Check(system.Id == "hirschmann-hc4900", "HC4900 profile id mismatch.");
        Check(system.ConfirmedBitrate == 125_000, "HC4900 default bitrate must be 125 kbit/s.");
        Check(system.Protocol.Contains("CANopen 2.0B", StringComparison.Ordinal),
            "HC4900 CANopen 2.0B marker missing.");
        Check(system.Nodes.Any(node => node.Address == 1 && node.Name.Contains("IC4600", StringComparison.Ordinal)),
            "HC4900 IC4600 Node-ID 1 missing.");
        Check(system.Nodes.Any(node => node.Address == 3 && node.Name.Contains("central", StringComparison.OrdinalIgnoreCase)),
            "HC4900 Mentor Node-ID 3 missing.");
        Check(system.Nodes.Any(node => node.Address == 15 && node.Name.Contains("Length", StringComparison.OrdinalIgnoreCase)),
            "HC4900 length/angle Node-ID 15 missing.");
        Check(system.Nodes.Any(node => node.Address == 0x3C), "HC4900 piston pressure Node-ID 0x3C missing.");
        Check(system.Nodes.Any(node => node.Address == 0x3D), "HC4900 rod pressure Node-ID 0x3D missing.");
        Check(new[] { "E61", "E62", "E63", "E64", "E94" }
                .All(code => system.Diagnostics.Any(item => item.Code == code)),
            "HC4900 CAN diagnostic code set is incomplete.");

        var machine = Hc4900Profile.CreateMachineProfile();
        Check(machine.Manufacturer == "Hirschmann" && machine.Model == "HC4900",
            "HC4900 MachineProfile identity mismatch.");
        Check(machine.Bitrate == 125_000 && machine.CanType.Contains("CANopen 2.0B", StringComparison.Ordinal),
            "HC4900 MachineProfile CAN settings mismatch.");
        Check(machine.KnownSignals.Count == 0 && machine.ExperimentalSignals.Count == 0,
            "HC4900 starter profile must not invent undocumented application signal mappings.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
