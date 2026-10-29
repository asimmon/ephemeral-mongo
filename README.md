<!-- omit from toc -->
# EphemeralMongo

[![ci](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/ci.yml?logo=github&label=ci)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/ci.yml) [![release](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/release.yml?logo=github&label=release)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/release.yml)

- [About](#about)
- [Installation](#installation)
- [Usage](#usage)
- [How it works](#how-it-works)
- [Faster mongod instances for tests](#faster-mongod-instances-for-tests)
- [MongoDB Enterprise and in-memory storage engine](#mongodb-enterprise-and-in-memory-storage-engine)
- [Windows Defender Firewall prompt](#windows-defender-firewall-prompt)
- [Reusing runners across tests](#reusing-runners-across-tests)
- [Troubleshooting](#troubleshooting)
- [Changelog](#changelog)

## About

**EphemeralMongo** is a .NET library that provides a simple way to run temporary and disposable MongoDB servers for integration tests and local debugging, without installing MongoDB or any external tools. The MongoDB binaries are downloaded at runtime. It is as simple as:

```csharp
using var runner = await MongoRunner.RunAsync();
Console.WriteLine(runner.ConnectionString);
```

Use the resulting connection string to access the MongoDB server. It will automatically be stopped and cleaned up when the `runner` is disposed. You can pass options to customize the server, such as the data directory, port, and replica set configuration. More details can be found in the [usage section](#usage).

List of features and capabilities:

- Multiple ephemeral and **isolated MongoDB databases** for running tests.
- A quick way to set up a MongoDB database for a local development environment.
- Access to **MongoDB 6**, **7**, **8**, and **9**.
- Access to **MongoDB Community** and **Enterprise editions**.
- Support for **single-node replica sets**, enabling transactions and change streams.
- _mongoexport_ and _mongoimport_ tools for [exporting](https://www.mongodb.com/docs/database-tools/mongoexport/) and [importing](https://www.mongodb.com/docs/database-tools/mongoimport/) collections.
- Support for .NET Framework 4.7.2, .NET Standard 2.1 (2.0 for `EphemeralMongo.v2`), .NET 8.0 and later.
- Support for Linux, macOS, and Windows, on x64 and ARM64.
- Supports **MongoDB C# driver version 2.x and 3.x**.

This project follows in the footsteps of [Mongo2Go](https://github.com/Mongo2Go/Mongo2Go) and expands upon its foundation.

## Installation

| Package | Description |
| --- | --- |
| [![nuget](https://img.shields.io/nuget/v/EphemeralMongo.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo/) | Install this package if you use MongoDB C# driver version 3.x (3.9.0 or later). |
| [![nuget](https://img.shields.io/nuget/v/EphemeralMongo.v2.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo.v2/) | Install this package if you use MongoDB C# driver version 2.x (2.28.0 or later). |

## Usage

Use `MongoRunner.RunAsync()` or `MongoRunner.Run()` methods to create a disposable instance that provides access to a **MongoDB connection string**, **import**, and **export tools**. An optional `MongoRunnerOptions` parameter can be provided to customize the MongoDB server.

```csharp
// All the following properties are OPTIONAL.
var options = new MongoRunnerOptions
{
    // The desired MongoDB version to download and use.
    // Possible values are V6, V7, V8 and V9. Default is V8.
    Version = MongoVersion.V8,

    // The desired MongoDB edition to download and use.
    // Possible values are Community and Enterprise. Default is Community.
    Edition = MongoEdition.Community,

    // If true, the MongoDB server will run as a single-node replica set. Default is false.
    UseSingleNodeReplicaSet = true,

    // Additional arguments to pass to the MongoDB server. Default is null.
    AdditionalArguments = ["--quiet"],

    // The port on which the MongoDB server will listen.
    // Default is null, which means a random available port will be assigned.
    MongoPort = 27017,

    // The directory where the MongoDB server will store its data.
    // Default is null, which means a temporary directory will be created.
    DataDirectory = "/path/to/data/",

    // Provide your own MongoDB binaries in this directory.
    // Default is null, which means the library will download them automatically.
    BinaryDirectory = "/path/to/mongo/bin/",

    // A delegate that receives the MongoDB server's standard output.
    StandardOutputLogger = Console.WriteLine,

    // A delegate that receives the MongoDB server's standard error output.
    StandardErrorLogger = Console.WriteLine,

    // Timeout for the MongoDB server to start. Default is 30 seconds.
    ConnectionTimeout = TimeSpan.FromSeconds(10),

    // Timeout for the replica set to be initialized. Default is 10 seconds.
    ReplicaSetSetupTimeout = TimeSpan.FromSeconds(5),

    // The duration for which temporary data directories will be kept.
    // Directories still used by a running mongod are always kept, and null disables the cleanup.
    // Ignored when you provide your own data directory. Default is 12 hours.
    DataDirectoryLifetime = TimeSpan.FromDays(1),

    // Override this property to provide your own HTTP transport.
    // Useful when behind a proxy or firewall. Default is a shared reusable instance.
    Transport = new HttpTransport(new HttpClient()),

    // Delay before checking for a new version of the MongoDB server and tools,
    // which is also how long the downloaded release information is cached. Default is 1 day.
    NewVersionCheckTimeout = TimeSpan.FromDays(2)
};
```

```csharp
// Disposing the runner will kill the MongoDB process (mongod) and delete the associated data directory
using (var runner = await MongoRunner.RunAsync(options))
{
    var database = new MongoClient(runner.ConnectionString).GetDatabase("default");

    // Do something with the database
    await database.CreateCollectionAsync("people");

    // Export a collection to a file. A synchronous version is also available.
    await runner.ExportAsync("default", "people", "/path/to/default.json");

    // Import a collection from a file. A synchronous version is also available.
    await runner.ImportAsync("default", "people", "/path/to/default.json");
}
```

## How it works

* At runtime, the MongoDB binaries (`mongod`, `mongoimport`, and `mongoexport`) are downloaded when needed (if not already present) and extracted to an `ephemeral-mongo` directory in your local application data directory, which also holds temporary files and default data directories:
  * Windows: `%LOCALAPPDATA%\ephemeral-mongo`
  * Linux: `~/.local/share/ephemeral-mongo`
  * macOS: `~/Library/Application Support/ephemeral-mongo`
* In CI, cache the `bin` subdirectory to avoid downloading the binaries on every run. Without network access, provide your own binaries with `BinaryDirectory`.
* Official download links are retrieved from [this JSON file](https://downloads.mongodb.org/current.json) for the server and [this JSON file](https://downloads.mongodb.org/tools/db/release.json) for the tools.
* These JSON files are cached in the same directory for `NewVersionCheckTimeout` and shared by all processes, so parallel test runs request them only once. If they can't be downloaded again, the outdated cached copy is used.
* Archives are only downloaded over HTTPS from mongodb.org, and SHA256 checksums are used to verify their integrity. Parallel test runs share a single download.
* On Windows ARM64, the x64 binaries run through Windows 11's x64 emulation, as MongoDB doesn't publish ARM64 builds for Windows.
* `MongoRunner.Run` or `MongoRunner.RunAsync` always starts a new `mongod` process. Unless `MongoPort` is set, it uses a random available port, and retries with another one if that port was taken in the meantime.
* The resulting connection string will depend on your options (`UseSingleNodeReplicaSet`).
* By default, a unique temporary data directory is used and deleted when the `runner` is disposed. Leftover data directories older than `DataDirectoryLifetime` are deleted on later runs.
* `mongod` does not outlive your process, even when it is killed without disposing the `runner` (SIGKILL, a test timeout, a cancelled CI run). Windows relies on a job object. Linux and macOS use a small `/bin/sh` guardian that reads a pipe held open by your process: when the process dies, the pipe closes and the guardian stops `mongod` and removes its data directory.

## Faster mongod instances for tests

By default, each `mongod` sizes its cache to about half of the machine's memory and writes diagnostic data (FTDC) to disk. When your tests start many instances in parallel, limit the cache and disable diagnostic data collection, like EphemeralMongo's own tests do:

```csharp
var options = new MongoRunnerOptions
{
    AdditionalArguments = ["--quiet", "--wiredTigerCacheSizeGB", "0.25", "--setParameter", "diagnosticDataCollectionEnabled=false"],
};
```

With the Enterprise edition, the [in-memory storage engine](#mongodb-enterprise-and-in-memory-storage-engine) is even faster.

## MongoDB Enterprise and in-memory storage engine

Make sure you are allowed to use MongoDB Enterprise binaries in your projects. The [official download page](https://www.mongodb.com/try/download/enterprise) states:

> MongoDB Enterprise Server is also available free of charge for evaluation and development purposes.

The Enterprise edition provides access to the in-memory storage engine, which is faster and more efficient for tests:

```csharp
var options = new MongoRunnerOptions
{
    Edition = MongoEdition.Enterprise,
    AdditionalArguments = ["--quiet", "--storageEngine", "inMemory", "--inMemorySizeGB", "0.25", "--setParameter", "diagnosticDataCollectionEnabled=false"],
};

using var runner = await MongoRunner.RunAsync(options);
// [...]
```

Like the WiredTiger cache, the in-memory storage engine uses about half of the machine's memory by default, so `--inMemorySizeGB` keeps parallel instances in check.

## Windows Defender Firewall prompt

On Windows, you might get a **Windows Defender Firewall prompt**.
This is because EphemeralMongo starts the `mongod.exe` process from your local app data directory, and `mongod.exe` tries to open an available port.

## Reusing runners across tests

Avoid calling `MongoRunner.Run` or `MongoRunner.RunAsync` concurrently, as this will create many `mongod` processes and make your operating system slower. Instead, try to use a single instance and reuse it - create as many databases as you need, one per test, for example.

For this, version 3.0.0 introduces a new *experimental* `MongoRunnerPool` class. Once created, you can rent and return `IMongoRunner` instances. Once a runner has been rented `maxRentalsPerRunner` times (100 by default), a new one is created, and the previous one is stopped when all its rentals are returned. This way, `mongod` processes will be reused, but not too often. This will avoid uncontrolled usage of system resources.

```csharp
var options = new MongoRunnerOptions
{
    Version = MongoVersion.V7,
    UseSingleNodeReplicaSet = true
};

using var pool = new MongoRunnerPool(options, maxRentalsPerRunner: 2);

var runner1 = await pool.RentAsync();
var runner2 = await pool.RentAsync();

try
{
    // Same connection string, same mongod process
    Console.WriteLine(runner1.ConnectionString);
    Console.WriteLine(runner2.ConnectionString);
}
finally
{
    pool.Return(runner1);
    pool.Return(runner2);
}

// This is a new mongod process, as the previous one was rented twice
var runner3 = await pool.RentAsync();
```

A runner that isn't rented for 10 seconds is stopped. `MongoPort` and `DataDirectory` can't be set when using a pool, as multiple runners may be alive at the same time.

As the pool is experimental, using it produces the `EMEX0001` diagnostic. Suppress it with `#pragma warning disable EMEX0001` or in your project file:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);EMEX0001</NoWarn>
</PropertyGroup>
```

## Troubleshooting

* When `mongod` fails to start, an `EphemeralMongoException` is thrown with the full process path, its arguments, and the errors logged by `mongod`.
* When `mongoimport` or `mongoexport` fails, an `EphemeralMongoException` is thrown with the command line and the tool's output.
* `StandardErrorLogger` also receives the library's own warnings, such as when retrying with another port or using outdated release information that couldn't be downloaded again.

## Changelog

See the [GitHub releases](https://github.com/asimmon/ephemeral-mongo/releases).
