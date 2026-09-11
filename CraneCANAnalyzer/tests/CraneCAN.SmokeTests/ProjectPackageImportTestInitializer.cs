using System.Runtime.CompilerServices;

internal static class ProjectPackageImportTestInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => ProjectPackageImportTests.Run();
}
