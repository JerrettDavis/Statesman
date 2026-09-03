namespace Statesman;

public static class FileSystemStatesmanExtensions
{
    public static StatesmanServiceBuilder UseFileSystemStore(
        this StatesmanServiceBuilder builder,
        string name,
        string rootDirectory,
        Action<FileSystemStateLedgerStoreOptions>? configure = null)
    {
        var options = new FileSystemStateLedgerStoreOptions { RootDirectory = rootDirectory };
        configure?.Invoke(options);
        return builder.UseStore(name, services => new FileSystemStateLedgerStore(
            name,
            options,
            services.GetService(typeof(TimeProvider)) as TimeProvider));
    }
}
