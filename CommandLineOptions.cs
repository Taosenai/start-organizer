internal sealed record CommandLineOptions(bool Apply, string? Undo, string? Config, bool Help, bool Version)
{
    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 1 && (args[0].ToLowerInvariant() is "-h" or "--help"))
            return new(false, null, null, true, false);
        if (args.Length == 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
            return new(false, null, null, false, true);

        bool apply = false;
        string? undo = null;
        string? config = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!seen.Add(arg)) throw new ArgumentException($"Repeated option: {arg}");
            switch (arg.ToLowerInvariant())
            {
                case "--apply": apply = true; break;
                case "--undo": undo = ReadPath(args, ref i); break;
                case "--config": config = ReadPath(args, ref i); break;
                default: throw new ArgumentException($"Unknown option '{arg}'. Use --help for usage.");
            }
        }
        if (undo is not null && config is not null)
            throw new ArgumentException("--config cannot be combined with --undo; undo uses the backup's recorded paths.");
        return new(apply, undo, config, false, false);
    }

    private static string ReadPath(string[] args, ref int index)
    {
        string option = args[index];
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith('-'))
            throw new ArgumentException($"{option} requires a path. Prefix a path beginning with '-' with .\\.");
        return args[index];
    }
}
