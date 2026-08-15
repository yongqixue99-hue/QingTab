using System;

internal static class ArgDump
{
    private static int Main(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            Console.WriteLine("{0}: <{1}>", index, args[index]);
        }

        return 0;
    }
}
