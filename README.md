# EphemeralMongo - temporary and disposable MongoDB for integration tests and local debugging

[![build](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/release.yml?logo=github)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/release.yml)

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
| **EphemeralMongo4** | All-in-one package for **MongoDB 4.4.18** on Linux, macOS and Windows | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo4.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo4/) |
| **EphemeralMongo5** | All-in-one package for **MongoDB 5.0.14** on Linux, macOS and Windows | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo5.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo5/) |
| **EphemeralMongo6** | All-in-one package for **MongoDB 6.0.4** on Linux, macOS and Windows  | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo6.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo6/) |


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
    MongoPort = 27017, // Default: random available port

    // EXPERIMENTAL - Only works on Windows and modern .NET (netcoreapp3.1, net5.0, net6.0, net7.0 and so on):
    // Ensures that all MongoDB child processes are killed when the current process is prematurely killed,
    // for instance when killed from the task manager or the IDE unit tests window.
    KillMongoProcessesWhenCurrentProcessExits = true // Default: false
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


## Reducing the download size

EphemeralMongo4, 5 and 6 are NuGet *metapackages* that reference dedicated runtime packages for both Linux, macOS and Windows.
As of now, there isn't a way to optimize NuGet package downloads for a specific operating system (see [#2](https://github.com/asimmon/ephemeral-mongo/issues/2)).
However, one can still avoid referencing the metapackage and directly reference the dependencies instead. Add MSBuild OS platform conditions and you'll get optimized NuGet imports for your OS and less downloads.

Instead of doing this:

```xml
<PackageReference Include="EphemeralMongo6" Version="1.0.0" />
```

Do this:
```xml
<PackageReference Include="EphemeralMongo.Core" Version="1.0.0" />
<PackageReference Include="EphemeralMongo6.runtime.linux-x64" Version="1.0.0" Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
<PackageReference Include="EphemeralMongo6.runtime.osx-x64" Version="1.0.0" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
<PackageReference Include="EphemeralMongo6.runtime.win-x64" Version="1.0.0" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```


## Tips

Avoid calling `MongoRunner.Run` concurrently, as this will create many `mongod` processes and make your operating system slower.
Instead, try to use a single instance and reuse it - create as many databases as you need, one per test for example.

Check out [this gist](https://gist.github.com/asimmon/612b2d54f1a0d2b4e1115590d456e0be) for an implementation of a reusable `IMongoRunner`.
