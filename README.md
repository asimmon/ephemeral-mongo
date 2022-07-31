# EphemeralMongo - temporary and disposable MongoDB for integration tests and local debugging

[![build](https://img.shields.io/github/workflow/status/asimmon/ephemeral-mongo/CI%20build?logo=github)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/ci.yml)

**EphemeralMongo** is a set of three NuGet packages wrapping the binaries of **MongoDB 4**, **5** and **6**.
Each package targets **.NET Standard 2.0**, which means you can use it with **.NET Framework 4.5.2** up to **.NET 6 and later**.

The supported operating systems are **Linux**, **macOS** and **Windows** on their **x64 architecture** versions only.
Each package provides access to:

* Multiple ephemeral and isolated MongoDB databases for tests running,
* A quick way to setup a MongoDB database for a local development environment,
* _mongoimport_ and _mongoexport_ tools in order to export and import collections.

This project is very much inspired from [Mongo2Go](https://github.com/Mongo2Go/Mongo2Go) but contains several improvements:

* Support for multiple major MongoDB versions that are copied to your build output,
* There is a separate NuGet package for each operating system and MongoDB version so it's easier to support new major versions,
* The latest MongoDB binaries are safely downloaded and verified by GitHub actions during the build or release workflow, reducing the Git repository size,
* There's less chances of memory, files and directory leaks. The startup is faster by using C# threading primitives such as `ManualResetEventSlim`.
* The CI tests the generated packages against .NET 4.6.2, .NET Core 3.1 and .NET 6 using the latest GitHub build agents for Ubuntu, macOS and Windows.


## Downloads

| Package             | Description                                                           | Link                                                                                                                       |
|---------------------|-----------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| **EphemeralMongo4** | All-in-one package for **MongoDB 4.4.15** on Linux, macOS and Windows | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo4.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo4/) |
| **EphemeralMongo5** | All-in-one package for **MongoDB 5.0.10** on Linux, macOS and Windows | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo5.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo5/) |
| **EphemeralMongo6** | All-in-one package for **MongoDB 6.0.0** on Linux, macOS and Windows  | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo6.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo6/) |


## Usage

Use the static `MongoRunner.Run()` method to create a disposable instance that provides access to a **MongoDB connection string**, **import** and **export tools**: 

```csharp
// All properties below are optional. The whole "options" instance is optional too!
var options = new MongoRunnerOptions
{
    UseSingleNodeReplicaSet = true, // Default: false
    StandardOuputLogger = line => Console.WriteLine(line), // Default: null
    StandardErrorLogger = line => Console.WriteLine(line), // Default: null
    DataDirectory = "/path/to/data/", // Default: null
    BinaryDirectory = "/path/to/mongo/bin/", // Default: null
    ConnectionTimeout = TimeSpan.FromSeconds(10), // Default: 30 seconds
    ReplicaSetSetupTimeout = TimeSpan.FromSeconds(5), // Default: 10 seconds
    AdditionalArguments = "--quiet", // Default: null
};

// Disposing the runner will kill the MongoDB process (mongod) and delete the associated data directory
using (var runner = MongoRunner.Run(options))
{
    var database = new MongoClient(runner.ConnectionString).GetDatabase("default");

    // Do something with the database
    database.CreateCollection("people");

    // Export a collection. Full method signature:
    // Export(string database, string collection, string outputFilePath, string? additionalArguments = null)
    runner.Export("default", "people", "/path/to/default.json");

    // Import a collection. Full method signature:
    // Import(string database, string collection, string inputFilePath, string? additionalArguments = null, bool drop = false)
    runner.Import("default", "people", "/path/to/default.json");
}
```


## How it works

* At build time, the MongoDB binaries (`mongod`, `mongoimport` and `mongoexport`) are copied to your project output directory,
* At runtime, the library chooses the right binaries for your operating system,
* `MongoRunner.Run` always starts a new `mongod` process with a random available port,
* The resulting connection string will depend on your options (`UseSingleNodeReplicaSet` and `AdditionalArguments`),
* By default, a unique temporary data directory is used.


## Tips

Try not to call `MongoRunner.Run` in parallel, as this will create many `mongod` processes and make your operating system slower.
Instead, try to use a single instance and reuse it - create as many databases as you need, one per test for example.