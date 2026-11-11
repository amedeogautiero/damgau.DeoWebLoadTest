using LoadTest.Agent;
using LoadTest.Agent.Service;

// `loadtest-agent install|uninstall ...` registra/rimuove il servizio del sistema operativo; senza argomenti di questo tipo avvia l'agente.
if (args.Length > 0 && args[0] is "install" or "uninstall")
    return await ServiceCommands.RunAsync(args, Console.Out, Console.Error);

await AgentHost.RunAsync(args);
return 0;
