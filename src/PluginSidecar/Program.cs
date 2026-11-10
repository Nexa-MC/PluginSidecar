if (args is ["--version"])
{
    Console.WriteLine("PluginSidecar 0.1.0-dev");
    return 0;
}
Console.WriteLine("PluginSidecar development scaffold. NexaCL Host integration is not implemented yet.");
Console.WriteLine("Usage: PluginSidecar --version");
return args.Length == 0 ? 0 : 2;
