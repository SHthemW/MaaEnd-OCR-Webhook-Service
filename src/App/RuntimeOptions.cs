namespace MaaEnd_Log_Retransmitter.App;

internal sealed class RuntimeOptions
{
    public bool Debug { get; init; }

    public static RuntimeOptions Parse(string[] args)
    {
        return new RuntimeOptions
        {
            Debug = args.Any(arg => string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase))
        };
    }
}
