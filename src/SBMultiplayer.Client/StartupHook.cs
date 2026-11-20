using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Entry point required by .NET for DOTNET_STARTUP_HOOKS:
// a class named exactly "StartupHook", outside any namespace, with "public static void Initialize()".
// It runs before the game's Main().
internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            string modDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            // Replay verification (organizers) runs the game once per replay: its own log, the players' one stays readable.
            bool verifying = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SBMULTI_VERIFY"));
            SBMultiplayer.Client.ModLog.Init(Path.Combine(modDir, verifying ? "SBMultiplayer-verify.log" : "SBMultiplayer.log"));
            SBMultiplayer.Client.ModLog.Info($"Startup hook loaded from {modDir}");
            if (!verifying)
            {
                SBMultiplayer.Client.Diagnostics.OnStartup(modDir);
            }

            // 0Harmony.dll is next to our DLL, not in the game folder:
            // help the runtime find it. Must be done BEFORE touching any Harmony type.
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(modDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };

            Boot();
        }
        catch (Exception e)
        {
            // Never crash the game because of the mod.
            SBMultiplayer.Client.ModLog.Error("Mod initialization failed", e);
        }
    }

    // Separate, non-inlined method: the JIT must load Harmony only after the resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Boot() => SBMultiplayer.Client.Plugin.Load();
}
