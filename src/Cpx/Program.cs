using Cpx;

const string Version = "0.1.0";

if (args.Length == 0)
{
    PrintHelp();
    return 2;
}

switch (args[0])
{
    case "--help" or "-h":
        PrintHelp();
        return 0;
    case "--version":
        Console.WriteLine($"cpx {Version}");
        return 0;
    case "--raw" or "--":
        if (args.Length < 2)
        {
            PrintHelp();
            return 2;
        }
        return Proxy.Run(args[1], args[2..], raw: args[0] == "--raw");
    case "explain":
        return Commands.Explain(args[1..]);
    case "test-rule":
        return Commands.TestRule(args[1..]);
    case "trust":
        return Commands.TrustRules(args[1..]);
    case "untrust":
        return Commands.UntrustRules();
    case "gain":
        return History.Gain(showHistory: args.Contains("--history"));
    case "hook":
        return Hook.Run();
    case "init":
        return Hook.Init(args[1..]);
    default:
        return Proxy.Run(args[0], args[1..], raw: false);
}

static void PrintHelp() => Console.WriteLine(
    """
    cpx - runs a command and trims its output for AI agents

    usage:
      cpx [--raw] <command> [args...]   run through the proxy (--raw: no filtering)
      cpx -- <command> [args...]        same, for commands named like the ones below
      cpx explain <command> [args...]   show which rule applies and what it does
      cpx test-rule --input <file> [--exit N] [--cwd <dir>] <command> [args...]
                                        apply a rule to recorded output
      cpx trust [--yes] | untrust       enable or disable this project's .cpx/rules
      cpx gain [--history]              bytes saved so far
      cpx init <claude|copilot> [--remove]
                                        install or remove the agent hook
      cpx hook                          hook entry point (reads the agent payload on stdin)
    """);
