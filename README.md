# EphemeralMongo - temporary and disposable MongoDB for integration tests and local debugging

[![ci](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/ci.yml?logo=github)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/ci.yml)
[![publish](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/release.yml?logo=github)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/release.yml)

**EphemeralMongo** is a set of multiple NuGet packages wrapping the binaries of **MongoDB 6, 7,** and **8**.
Each package targets **.NET Standard 2.0**, which means you can use it with **.NET Framework 4.5.2** up to **.NET 9 and later**.

The supported operating systems are **Linux** (x64), **macOS** (arm64), and **Windows** (x64).
Each package provides access to:

* Multiple ephemeral and isolated MongoDB databases for running tests,
* A quick way to set up a MongoDB database for a local development environment,
* _mongoimport_ and _mongoexport_ tools for exporting and importing collections,
* Support for **single-node replica sets**, enabling transactions and change streams.

This project follows in the footsteps of [Mongo2Go](https://github.com/Mongo2Go/Mongo2Go) and expands upon its foundation.

| Package             | Description                                                           | Link                                                                                                                       |
|---------------------|-----------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| **EphemeralMongo6** | All-in-one package for **MongoDB 6.0.21** on Linux, macOS, and Windows  | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo6.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo6/) |
| **EphemeralMongo7** | All-in-one package for **MongoDB 7.0.18** on Linux, macOS, and Windows  | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo7.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo7/) |
| **EphemeralMongo8** | All-in-one package for **MongoDB 8.0.6** on Linux, macOS, and Windows  | [![nuget](https://img.shields.io/nuget/v/EphemeralMongo8.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo8/) |

## Usage

Use the static `MongoRunner.Run()` method to create a disposable instance that provides access to a **MongoDB connection string**, **import**, and **export tools**:

```csharp
// All properties below are optional. The whole "options" instance is optional too!
var options = new MongoRunnerOptions
{
    UseSingleNodeReplicaSet = true, // Default: false
    StandardOutputLogger = line => Console.WriteLine(line), // Default: null
    StandardErrorLogger = line => Console.WriteLine(line), // Default: null
    DataDirectory = "/path/to/data/", // Default: null
    BinaryDirectory = "/path/to/mongo/bin/", // Default: null
    ConnectionTimeout = TimeSpan.FromSeconds(10), // Default: 30 seconds
    ReplicaSetSetupTimeout = TimeSpan.FromSeconds(5), // Default: 10 seconds
    AdditionalArguments = "--quiet", // Default: null
    MongoPort = 27017, // Default: random available port
    DataDirectoryLifetime = TimeSpan.FromDays(1), // Default: 12 hours
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

* At build time, the MongoDB binaries (`mongod`, `mongoimport`, and `mongoexport`) are copied to your project output directory,
* At runtime, the library chooses the right binaries for your operating system,
* `MongoRunner.Run` always starts a new `mongod` process with a random available port,
* The resulting connection string will depend on your options (`UseSingleNodeReplicaSet` and `AdditionalArguments`),
* By default, a unique temporary data directory is used.

## Reducing the download size

EphemeralMongo6, 7, and 8 are NuGet *metapackages* that reference dedicated runtime packages for Linux, macOS, and Windows.
As of now, there isn't a way to optimize NuGet package downloads for a specific operating system (see [#2](https://github.com/asimmon/ephemeral-mongo/issues/2)).
However, one can still avoid referencing the metapackage and directly reference the dependencies instead. Add MSBuild OS platform conditions and you'll get optimized NuGet imports for your OS and fewer downloads.

Instead of doing this:

```xml
<PackageReference Include="EphemeralMongo8" Version="*" />
```

Do this:
```xml
<PackageReference Include="EphemeralMongo.Core" Version="*" />
<PackageReference Include="EphemeralMongo8.runtime.linux-x64" Version="*" Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
<PackageReference Include="EphemeralMongo8.runtime.osx-arm64" Version="*" Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
<PackageReference Include="EphemeralMongo8.runtime.win-x64" Version="*" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```

## Windows Defender Firewall prompt

On Windows, you might get a **Windows Defender Firewall prompt**.
This is because EphemeralMongo starts the `mongod.exe` process from your build output directory, and `mongod.exe` tries to open an available port ([see here](https://github.com/asimmon/ephemeral-mongo/blob/1.0.0/src/EphemeralMongo.Core/MongoRunner.cs#L64)).

## Optimization tips

Avoid calling `MongoRunner.Run` concurrently, as this will create many `mongod` processes and make your operating system slower.
Instead, try to use a single instance and reuse it—create as many databases as you need, one per test, for example.

Check out [this gist](https://gist.github.com/asimmon/612b2d54f1a0d2b4e1115590d456e0be) for an implementation of a reusable `IMongoRunner`.
