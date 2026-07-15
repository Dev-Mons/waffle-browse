using Waffle.Browse.Core.Search.Indexing;

if (args.Length != 0)
{
    Console.Error.WriteLine("Waffle.Browse.Indexer는 명령줄 query 또는 path 인수를 받지 않습니다.");
    return 2;
}

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
EventHandler processExitHandler = (_, _) => shutdown.Cancel();

Console.CancelKeyPress += cancelHandler;
AppDomain.CurrentDomain.ProcessExit += processExitHandler;
try
{
    var host = new NamedPipeFileIndexHost();
    await host.RunAsync(shutdown.Token).ConfigureAwait(false);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
}

return 0;
