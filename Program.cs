if (args.Length > 0 && args[0] == "--selftest")
{
    System.Console.WriteLine(Solar.Tests.SanityChecks.Run());
    return;
}

// Maintenance: rewrite the source Content catalogs from the in-code BuiltIn() lists. Pass a target
// directory, else the output Content folder. Needed because the runtime merge only adds new entries.
if (args.Length > 0 && args[0] == "--regen-catalogs")
{
    string dir = args.Length > 1 ? args[1] : System.IO.Path.Combine(System.AppContext.BaseDirectory, "Content");
    Solar.Parts.PartCatalog.WriteTemplate(dir);
    Solar.Parts.ModuleCatalog.WriteTemplate(dir);
    System.Console.WriteLine("Catalogs regenerated in " + dir);
    return;
}

using var game = new Solar.SolarGame();
game.Run();
