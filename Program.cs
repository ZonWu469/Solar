if (args.Length > 0 && args[0] == "--selftest")
{
    System.Console.WriteLine(Solar.Tests.SanityChecks.Run());
    return;
}

using var game = new Solar.SolarGame();
game.Run();
