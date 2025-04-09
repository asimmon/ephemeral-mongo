namespace EphemeralMongo;

internal sealed class MongoProcessFactory : IMongoProcessFactory
{
    public IMongoProcess CreateMongoProcess(MongoRunnerOptions options, MongoProcessKind processKind, string executablePath, string[] arguments)
    {
        var escapedArguments = new string[arguments.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            escapedArguments[i] = ProcessArgument.Escape(arguments[i]);
        }

        return processKind == MongoProcessKind.Mongod
            ? new MongodProcess(options, executablePath, escapedArguments)
            : new MongoImportExportProcess(options, executablePath, escapedArguments);
    }
}