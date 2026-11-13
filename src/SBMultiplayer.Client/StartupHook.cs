using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Point d'entrée imposé par .NET pour DOTNET_STARTUP_HOOKS :
// une classe nommée exactement "StartupHook", hors namespace, avec "public static void Initialize()".
// Elle s'exécute avant le Main() du jeu.
internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            string modDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            SBMultiplayer.Client.ModLog.Init(Path.Combine(modDir, "SBMultiplayer.log"));
            SBMultiplayer.Client.ModLog.Info($"Startup hook chargé depuis {modDir}");

            // 0Harmony.dll est à côté de notre DLL, pas dans le dossier du jeu :
            // on aide le runtime à le trouver. À faire AVANT de toucher au moindre type Harmony.
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(modDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };

            Boot();
        }
        catch (Exception e)
        {
            // Ne jamais faire planter le jeu à cause du mod.
            SBMultiplayer.Client.ModLog.Error("Échec de l'initialisation du mod", e);
        }
    }

    // Méthode séparée et non inlinée : le JIT ne doit charger Harmony qu'après l'enregistrement du resolver.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Boot() => SBMultiplayer.Client.Plugin.Load();
}
